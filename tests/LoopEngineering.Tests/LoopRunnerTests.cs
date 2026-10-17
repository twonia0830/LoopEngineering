using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class LoopRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loopeng-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LoopConfig Config(int maxLoops = 3) => new()
    {
        GoalName = "My Goal",
        GoalDescription = "Write hello.txt",
        CriteriaText = "hello.txt exists",
        MaxLoops = maxLoops,
        WorkingDirectories = [_dir]
    };

    [Fact]
    public async Task Stops_when_evaluator_reports_criteria_met()
    {
        var agent = new FakeAgent(evaluations: ["{\"criteriaMet\": false, \"score\": 30}", "{\"criteriaMet\": true, \"score\": 100}"]);
        var runner = new LoopRunner(new FakeFactory(agent));

        runner.Start(Config(maxLoops: 5));
        await runner.WaitForCompletionAsync();

        var snap = runner.GetSnapshot();
        Assert.Equal(RunStatus.CriteriaMet, snap.Status);
        Assert.Equal(2, snap.Rounds.Count);
        Assert.Equal(100, snap.Rounds[1].Evaluation!.Score);

        var progress = File.ReadAllText(Path.Combine(_dir, "My_Goal_progress.md"));
        Assert.Contains("### Round 1/5 — NOT MET (score 30/100)", progress);
        Assert.Contains("### Round 2/5 — CRITERIA MET", progress);
        Assert.Contains("Criteria met after 2 round(s).", progress);
    }

    [Fact]
    public async Task Ends_with_max_loops_reached_and_feeds_progress_into_next_prompt()
    {
        var agent = new FakeAgent(evaluations: ["{\"criteriaMet\": false, \"nextSteps\": \"do the thing\"}"]);
        var runner = new LoopRunner(new FakeFactory(agent));

        runner.Start(Config(maxLoops: 2));
        await runner.WaitForCompletionAsync();

        Assert.Equal(RunStatus.MaxLoopsReached, runner.GetSnapshot().Status);
        var secondWorkerPrompt = agent.Prompts.Where(p => p.Phase == RoundPhase.Worker).ElementAt(1).Prompt;
        Assert.Contains("do the thing", secondWorkerPrompt);
    }

    [Fact]
    public async Task Stop_cancels_running_turn()
    {
        var agent = new FakeAgent(evaluations: [], blockWorker: true);
        var runner = new LoopRunner(new FakeFactory(agent));

        runner.Start(Config());
        await agent.WorkerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Stop();
        await runner.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var snap = runner.GetSnapshot();
        Assert.Equal(RunStatus.Stopped, snap.Status);
        Assert.Equal(RoundStatus.Cancelled, snap.Rounds.Single().Status);
        Assert.Contains("Stopped by user", File.ReadAllText(snap.ProgressFilePath!));
    }

    [Fact]
    public async Task Records_subagents()
    {
        var agent = new FakeAgent(evaluations: ["{\"criteriaMet\": true}"], spawnSubagent: true);
        var runner = new LoopRunner(new FakeFactory(agent));

        runner.Start(Config());
        await runner.WaitForCompletionAsync();

        var sub = runner.GetSnapshot().Rounds.Single().Subagents.Single();
        Assert.Equal("explorer", sub.Name);
        Assert.True(sub.Succeeded);
    }

    [Fact]
    public void Start_rejects_invalid_config()
    {
        var runner = new LoopRunner(new FakeFactory(new FakeAgent([])));
        Assert.Throws<ArgumentException>(() => runner.Start(new LoopConfig()));
    }

    [Theory]
    [InlineData("```json\n{\"criteriaMet\": true, \"score\": 90}\n```", true, 90)]
    [InlineData("Sure! {\"CriteriaMet\": \"true\"} done", true, null)]
    [InlineData("I think it is done", false, null)]
    public void Parses_evaluator_output(string raw, bool met, int? score)
    {
        var eval = EvaluationParser.Parse(raw);
        Assert.Equal(met, eval.CriteriaMet);
        Assert.Equal(score, eval.Score);
    }

    [Fact]
    public void Runtime_environment_hides_ambient_tokens()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "ambient");
        Environment.SetEnvironmentVariable("MY_LOOP_TOKEN", "explicit");
        try
        {
            var env = CopilotClients.BuildRuntimeEnvironment("MY_LOOP_TOKEN");
            Assert.False(env.ContainsKey("GH_TOKEN"));
            Assert.False(env.ContainsKey("MY_LOOP_TOKEN"));
            Assert.True(env.ContainsKey("PATH"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            Environment.SetEnvironmentVariable("MY_LOOP_TOKEN", null);
        }
    }

    [Fact]
    public async Task Sums_token_usage_per_round_and_run()
    {
        var agent = new FakeAgent(evaluations: ["{\"criteriaMet\": false}"]);
        var runner = new LoopRunner(new FakeFactory(agent));
        var roundEvents = 0;
        runner.RoundCompleted += () => Interlocked.Increment(ref roundEvents);

        runner.Start(Config(maxLoops: 2));
        await runner.WaitForCompletionAsync();

        var snap = runner.GetSnapshot();
        Assert.All(snap.Rounds, r => Assert.Equal(1200, r.Usage.TotalTokens));
        Assert.Equal(2400, snap.TotalUsage.TotalTokens);
        Assert.Equal(4, snap.TotalUsage.ApiCalls);
        Assert.Equal(2, snap.TotalUsage.PremiumRequests); // billed per prompt, not per API call
        Assert.Equal(2, snap.TotalUsage.UserRequests);
        Assert.Equal(0.5, snap.TotalUsage.AiCredits);
        Assert.Equal(4, snap.TotalUsage.Models["gpt-5"]);
        Assert.Contains("**Models used:** worker gpt-5 ×2 (0.25 credits); evaluator -", File.ReadAllText(snap.ProgressFilePath!));
        Assert.Equal(3, roundEvents); // two rounds + run end
        Assert.Contains("Total tokens: in 2,000 / out 400", File.ReadAllText(snap.ProgressFilePath!));
    }

    private sealed class FakeFactory(ILoopAgent agent) : ILoopAgentFactory
    {
        public ILoopAgent Create(LoopConfig config) => agent;
    }

    private sealed class FakeAgent(string[] evaluations, bool blockWorker = false, bool spawnSubagent = false) : ILoopAgent
    {
        private int _evalIndex;
        public List<AgentTurnRequest> Prompts { get; } = [];
        public TaskCompletionSource WorkerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
        {
            Prompts.Add(request);
            if (request.Phase == RoundPhase.Worker)
            {
                WorkerEntered.TrySetResult();
                if (blockWorker) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (spawnSubagent)
                {
                    observer.OnSubagentStarted("t1", "explorer", null);
                    observer.OnSubagentFinished("t1", true, null);
                }
                // One prompt with a tool-loop follow-up: two API calls, but billed once.
                observer.OnUsage(new UsageSample(600, 120, 300, 0, "gpt-5"));
                observer.OnUsage(new UsageSample(400, 80, 200, 0, "gpt-5"));
                observer.OnBilling(1, 1, 0.25);
                observer.OnOutputDelta("working…");
                return "## Round summary\nDid work.";
            }
            var eval = evaluations[Math.Min(_evalIndex++, evaluations.Length - 1)];
            return eval;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
