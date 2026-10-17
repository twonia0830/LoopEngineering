using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class RepoGuideTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("loopeng-a-").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("loopeng-b-").FullName;

    public void Dispose()
    {
        Directory.Delete(_a, recursive: true);
        Directory.Delete(_b, recursive: true);
    }

    private LoopConfig Config(params string[] folders) => new()
    {
        GoalName = "g", GoalDescription = "Add a feature", CriteriaText = "done", MaxLoops = 2,
        WorkingDirectories = [.. folders], Model = "gpt-5-mini", EvaluatorModel = "claude-haiku-5.5"
    };

    [Theory]
    [InlineData("# Repo\n- build: dotnet build", "# Repo\n- build: dotnet build")]
    [InlineData("```markdown\n# Repo\nbody\n```", "# Repo\nbody")]
    [InlineData("Here is the guide:\n\n# Repo\nbody", "# Repo\nbody")]
    [InlineData("NO_CHANGES", null)]
    [InlineData("I could not explore the repo.", null)]
    public void Parses_guide_replies(string reply, string? expected) =>
        Assert.Equal(expected is null ? null : expected + Environment.NewLine, RepoGuide.ParseReply(reply));

    [Fact]
    public async Task Creates_missing_guides_only_and_feeds_them_to_rounds()
    {
        File.WriteAllText(RepoGuide.PathFor(_b), "# Existing guide for B\n");
        var agent = new GuideAgent(criteriaMet: false);
        var runner = new LoopRunner(new Factory(agent));

        var config = Config(_a, _b);
        config.MaxLoops = 1;
        runner.Start(config);
        await runner.WaitForCompletionAsync();

        // Only folder A had no guide; it was written in A itself, by the guide model.
        var guideTurn = Assert.Single(agent.Turns, t => t.Phase == RoundPhase.Guide);
        Assert.Equal(_a, guideTurn.WorkingDirectory);
        Assert.Contains("Do NOT modify", guideTurn.Prompt);
        Assert.Equal("# Guide written for A" + Environment.NewLine, File.ReadAllText(RepoGuide.PathFor(_a)));
        Assert.Equal("# Existing guide for B\n", File.ReadAllText(RepoGuide.PathFor(_b)));

        var worker = agent.Turns.First(t => t.Phase == RoundPhase.Worker).Prompt;
        var evaluator = agent.Turns.First(t => t.Phase == RoundPhase.Evaluator).Prompt;
        foreach (var prompt in new[] { worker, evaluator })
        {
            Assert.Contains("# Guide written for A", prompt);
            Assert.Contains("# Existing guide for B", prompt);
        }
        Assert.Contains("Do not edit AGENTS.md", worker);

        // Not successful, so no update step.
        Assert.DoesNotContain(agent.Turns, t => t.Prompt.Contains("completed successfully"));
    }

    [Fact]
    public async Task Updates_guides_after_success_and_counts_their_cost()
    {
        File.WriteAllText(RepoGuide.PathFor(_a), "# Guide A\n- build: dotnet build\n");
        File.WriteAllText(RepoGuide.PathFor(_b), "# Guide B\n");
        var agent = new GuideAgent(criteriaMet: true);
        var runner = new LoopRunner(new Factory(agent));
        var recorded = new List<double?>();
        runner.RoundUsageRecorded += u => recorded.Add(u.AiCredits);

        runner.Start(Config(_a, _b));
        await runner.WaitForCompletionAsync();

        var snap = runner.GetSnapshot();
        Assert.Equal(RunStatus.CriteriaMet, snap.Status);
        // A: the agent proposes a new fact; B: NO_CHANGES leaves the file alone.
        Assert.Contains("- tests: dotnet test --filter Unit", File.ReadAllText(RepoGuide.PathFor(_a)));
        Assert.Equal("# Guide B\n", File.ReadAllText(RepoGuide.PathFor(_b)));

        var update = agent.Turns.First(t => t.Phase == RoundPhase.Guide && t.WorkingDirectory == _a).Prompt;
        Assert.Contains("Did the work, tests use --filter Unit", update); // the worker's round summary reaches the update step

        // Round (worker 0.2 + evaluator 0.1) + two update steps (0.05 each).
        Assert.Equal(0.4, snap.TotalUsage.AiCredits!.Value, precision: 6);
        Assert.Equal(0.4, recorded.Sum()!.Value, precision: 6);
        Assert.Contains("AGENTS.md updated with learnings from this run", File.ReadAllText(snap.ProgressFilePath!));
    }

    [Fact]
    public async Task Guide_failure_does_not_stop_the_run()
    {
        var agent = new GuideAgent(criteriaMet: true, failGuide: true);
        var runner = new LoopRunner(new Factory(agent));

        runner.Start(Config(_a));
        await runner.WaitForCompletionAsync();

        Assert.Equal(RunStatus.CriteriaMet, runner.GetSnapshot().Status);
        Assert.False(File.Exists(RepoGuide.PathFor(_a)));
        Assert.Contains(runner.GetSnapshot().Log, l => l.Level == "warn" && l.Message.Contains("AGENTS.md step"));
    }

    [Fact]
    public async Task Starting_point_file_guides_the_writer()
    {
        var flow = Path.Combine(_b, "flow.md");
        File.WriteAllText(flow, "# Flow\nStart at `OrderController.Submit`, then `SamplingService.Plan`.");
        var agent = new GuideAgent(criteriaMet: false);
        var runner = new LoopRunner(new Factory(agent));
        var config = Config(_a);
        config.MaxLoops = 1;
        config.AgentsMdStartingPointFile = flow;

        runner.Start(config);
        await runner.WaitForCompletionAsync();

        var prompt = Assert.Single(agent.Turns, t => t.Phase == RoundPhase.Guide).Prompt;
        Assert.Contains("Start here", prompt);
        Assert.Contains("Start at `OrderController.Submit`", prompt);
        Assert.Contains(runner.GetSnapshot().Log, l => l.Message.Contains("starting from flow.md"));
    }

    [Fact]
    public async Task Regenerate_rewrites_existing_guide_with_old_one_as_reference()
    {
        File.WriteAllText(RepoGuide.PathFor(_a), "# Old guide\n- gotcha: run migrations first\n");
        var agent = new GuideAgent(criteriaMet: false);
        var runner = new LoopRunner(new Factory(agent));
        var config = Config(_a);
        config.MaxLoops = 1;
        config.RegenerateAgentsMd = true;

        runner.Start(config);
        await runner.WaitForCompletionAsync();

        var prompt = Assert.Single(agent.Turns, t => t.Phase == RoundPhase.Guide).Prompt;
        Assert.Contains("which you are replacing", prompt);
        Assert.Contains("gotcha: run migrations first", prompt);
        Assert.Equal("# Guide written for A" + Environment.NewLine, File.ReadAllText(RepoGuide.PathFor(_a)));
        Assert.Contains(runner.GetSnapshot().Log, l => l.Message.Contains("AGENTS.md regenerated"));
    }

    [Fact]
    public void Missing_starting_point_file_blocks_start()
    {
        var config = Config(_a);
        config.AgentsMdStartingPointFile = Path.Combine(_a, "nope.md");
        Assert.Contains(config.Validate(), e => e.Contains("Starting point file not found"));

        config.UseAgentsMd = false; // irrelevant when the feature is off
        Assert.DoesNotContain(config.Validate(), e => e.Contains("Starting point"));
    }

    [Fact]
    public void Guide_model_defaults_to_evaluator()
    {
        var config = Config(_a);
        Assert.Equal("claude-haiku-5.5", config.ModelFor(RoundPhase.Guide));
        config.AgentsMdModel = "gpt-5-mini";
        Assert.Equal("gpt-5-mini", config.ModelFor(RoundPhase.Guide));
    }

    private sealed class Factory(ILoopAgent agent) : ILoopAgentFactory
    {
        public ILoopAgent Create(LoopConfig config) => agent;
    }

    private sealed class GuideAgent(bool criteriaMet, bool failGuide = false) : ILoopAgent
    {
        public List<AgentTurnRequest> Turns { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
        {
            Turns.Add(request);
            switch (request.Phase)
            {
                case RoundPhase.Guide:
                    if (failGuide) throw new InvalidOperationException("guide boom");
                    observer.OnBilling(0, 1, 0.05);
                    if (request.Prompt.Contains("completed successfully"))
                        return Task.FromResult(request.WorkingDirectory!.Contains("loopeng-a-")
                            ? "# Guide A\n- build: dotnet build\n- tests: dotnet test --filter Unit"
                            : "NO_CHANGES");
                    return Task.FromResult("# Guide written for A");
                case RoundPhase.Worker:
                    observer.OnBilling(0, 1, 0.2);
                    return Task.FromResult("Work.\n## Round summary\nDid the work, tests use --filter Unit.");
                default:
                    observer.OnBilling(0, 1, 0.1);
                    return Task.FromResult(criteriaMet ? "{\"criteriaMet\": true}" : "{\"criteriaMet\": false}");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
