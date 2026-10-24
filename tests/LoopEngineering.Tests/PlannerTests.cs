using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;
using LoopEngineering.Core.Planning;

namespace LoopEngineering.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("loopeng-plan-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LoopConfig Config(int maxLoops = 5) => new()
    {
        GoalName = "g", GoalDescription = "Build a CSV importer", CriteriaText = "importer works\nREADME documents it",
        MaxLoops = maxLoops, WorkingDirectories = [_dir], UseAgentsMd = false
    };

    [Fact]
    public void Parses_planner_reply_with_issues_tasks_and_checks()
    {
        const string reply = """
            Here is my review:
            ```json
            {"summary":"Two steps.","issues":[{"severity":"blocker","message":"'works' is vague","suggestion":"Name the sample file"},{"severity":"info","message":"No tests yet"}],
             "tasks":[{"title":"Parser","goal":"Parse rows","criteria":"- parses sample.csv"},{"title":"Docs","criteria":"- README has usage"}],
             "suggestedChecks":[{"command":"dotnet test","verifies":"parser"}]}
            ```
            """;
        var plan = PlanParser.Parse(reply, "goal", "criteria");

        Assert.Equal("Two steps.", plan.Summary);
        Assert.True(plan.HasBlockers);
        Assert.Equal(PlanIssueSeverity.Info, plan.Issues[1].Severity);
        Assert.Equal(["Parser", "Docs"], plan.Tasks.Select(t => t.Title));
        Assert.Equal("dotnet test", plan.SuggestedChecks.Single().Command);
    }

    [Fact]
    public void Plan_without_tasks_falls_back_to_the_whole_goal()
    {
        var plan = PlanParser.Parse("""{"summary":"fine","issues":[]}""", "the goal", "the criteria");
        var task = Assert.Single(plan.Tasks);
        Assert.Equal("the criteria", task.Criteria);
    }

    [Fact]
    public void Unreadable_reply_throws()
    {
        Assert.Throws<InvalidOperationException>(() => PlanParser.Parse("I could not plan this.", "g", "c"));
    }

    [Fact]
    public void Fingerprint_changes_with_goal_or_criteria()
    {
        var config = Config();
        var before = LoopPlan.Fingerprint(config);
        config.CriteriaText += "\nmore";
        Assert.NotEqual(before, LoopPlan.Fingerprint(config));
    }

    [Fact]
    public void Planner_model_follows_evaluator_on_the_same_backend()
    {
        var config = Config();
        config.EvaluatorBackend = AgentBackend.ClaudeCode;
        config.EvaluatorModel = "claude-sonnet-5-5";
        Assert.Equal(AgentBackend.ClaudeCode, config.BackendFor(RoundPhase.Planner));
        Assert.Equal("claude-sonnet-5-5", config.ModelFor(RoundPhase.Planner));

        config.PlannerBackend = HelperBackendChoice.Antigravity;
        Assert.Equal("", config.ModelFor(RoundPhase.Planner));
        config.PlannerModel = "gemini-4-argon-high";
        Assert.Equal("gemini-4-argon-high", config.ModelFor(RoundPhase.Planner));
    }

    [Fact]
    public async Task Plan_service_runs_the_planner_and_tracks_approval()
    {
        var agent = new ScriptedAgent(planReply: """{"summary":"ok","tasks":[{"title":"A","criteria":"a done"},{"title":"B","criteria":"b done"}]}""");
        var service = new PlanService(Path.Combine(_dir, "plan.json"), new Factory(agent));
        var config = Config();

        await service.PlanAsync(config);

        Assert.Null(service.Error);
        Assert.Equal(2, service.Plan!.Tasks.Count);
        Assert.Contains("Do NOT modify", agent.Turns.Single().Prompt);
        Assert.Equal(RoundPhase.Planner, agent.Turns.Single().Phase);
        Assert.True(service.IsCurrentFor(config));
        Assert.False(service.IsApprovedFor(config));

        service.Approve();
        Assert.True(service.IsApprovedFor(config));

        // Editing clears the approval; a changed goal makes the plan stale.
        var edited = service.Plan!;
        edited.Tasks[1].Criteria = "b done and tested";
        service.Update(edited);
        Assert.False(service.IsApprovedFor(config));
        config.GoalDescription = "Something else";
        Assert.False(service.IsCurrentFor(config));

        // The plan survives a restart.
        var reloaded = new PlanService(Path.Combine(_dir, "plan.json"), new Factory(agent));
        Assert.Equal("b done and tested", reloaded.Plan!.Tasks[1].Criteria);
    }

    [Fact]
    public async Task Runner_works_task_by_task_then_checks_the_overall_criteria()
    {
        var agent = new ScriptedAgent();
        var runner = new LoopRunner(new Factory(agent));
        var plan = new LoopPlan
        {
            Approved = true,
            Tasks = [new PlanTask { Title = "Parser", Criteria = "parses sample.csv" }, new PlanTask { Title = "Docs", Criteria = "README has usage" }]
        };

        runner.Start(Config(), plan);
        await runner.WaitForCompletionAsync();

        var snap = runner.GetSnapshot();
        Assert.Equal(RunStatus.CriteriaMet, snap.Status);
        Assert.Equal(2, snap.Rounds.Count);
        Assert.Equal(["Parser", "Docs"], snap.Rounds.Select(r => r.TaskTitle));
        Assert.True(snap.Rounds[1].FinalCheck);
        Assert.All(snap.Plan!.Tasks, t => Assert.Equal(PlanTaskStatus.Passed, t.Status));

        var workerPrompts = agent.Turns.Where(t => t.Phase == RoundPhase.Worker).Select(t => t.Prompt).ToList();
        Assert.Contains("## Current task: Parser", workerPrompts[0]);
        Assert.Contains("## Current task: Docs", workerPrompts[1]);
        Assert.Contains("[done] Parser", workerPrompts[1]);

        var evaluatorPrompts = agent.Turns.Where(t => t.Phase == RoundPhase.Evaluator).Select(t => t.Prompt).ToList();
        Assert.Equal(3, evaluatorPrompts.Count); // task 1, task 2, final overall check
        Assert.Contains("judge only these", evaluatorPrompts[0]);
        Assert.Contains("importer works", evaluatorPrompts[2]); // the original criteria

        var progress = File.ReadAllText(snap.ProgressFilePath!);
        Assert.Contains("### Plan (2 tasks, approved)", progress);
        Assert.Contains("TASK PASSED", progress);
        Assert.Contains("Final check of the overall criteria:** met", progress);
    }

    [Fact]
    public async Task Single_task_plan_ends_when_the_task_passes()
    {
        var agent = new ScriptedAgent();
        var runner = new LoopRunner(new Factory(agent));
        runner.Start(Config(), new LoopPlan { Tasks = [new PlanTask { Title = "Whole goal", Criteria = "done" }] });
        await runner.WaitForCompletionAsync();

        Assert.Equal(RunStatus.CriteriaMet, runner.GetSnapshot().Status);
        Assert.Equal(1, agent.Turns.Count(t => t.Phase == RoundPhase.Evaluator)); // no extra final check
    }

    [Fact]
    public async Task Failed_final_check_keeps_the_loop_going_on_the_overall_goal()
    {
        var agent = new ScriptedAgent(finalPasses: [false, true]);
        var runner = new LoopRunner(new Factory(agent));
        runner.Start(Config(), new LoopPlan
        {
            Tasks = [new PlanTask { Title = "A", Criteria = "a" }, new PlanTask { Title = "B", Criteria = "b" }]
        });
        await runner.WaitForCompletionAsync();

        var snap = runner.GetSnapshot();
        Assert.Equal(RunStatus.CriteriaMet, snap.Status);
        Assert.Equal(3, snap.Rounds.Count);
        Assert.Null(snap.Rounds[2].TaskTitle); // after all tasks passed, rounds target the overall criteria
    }

    private sealed class Factory(ILoopAgent agent) : ILoopAgentFactory
    {
        public ILoopAgent Create(LoopConfig config) => agent;
    }

    /// <summary>Tasks always pass; overall checks (no task in the prompt) follow <paramref name="finalPasses"/>.</summary>
    private sealed class ScriptedAgent(string? planReply = null, bool[]? finalPasses = null) : ILoopAgent
    {
        private int _finalIndex;
        public List<AgentTurnRequest> Turns { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
        {
            Turns.Add(request);
            return Task.FromResult(request.Phase switch
            {
                RoundPhase.Planner => planReply ?? "{}",
                RoundPhase.Worker => "## Round summary\nDone.",
                _ when request.Prompt.Contains("judge only these") => "{\"criteriaMet\": true}",
                _ => (finalPasses is null || finalPasses[Math.Min(_finalIndex++, finalPasses.Length - 1)])
                    ? "{\"criteriaMet\": true}"
                    : "{\"criteriaMet\": false, \"nextSteps\": \"fix the README\"}"
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
