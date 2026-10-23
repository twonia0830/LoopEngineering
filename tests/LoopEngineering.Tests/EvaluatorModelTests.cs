using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class EvaluatorModelTests
{
    [Fact]
    public void Evaluator_falls_back_to_worker_model_and_effort()
    {
        var config = new LoopConfig { Model = "gpt-5-mini", ReasoningEffort = "high" };

        Assert.Equal("gpt-5-mini", config.ModelFor(RoundPhase.Evaluator));
        Assert.Equal("high", config.ReasoningEffortFor(RoundPhase.Evaluator));
    }

    [Fact]
    public void Different_evaluator_model_uses_its_own_default_effort()
    {
        var config = new LoopConfig { Model = "gpt-5-mini", ReasoningEffort = "high", EvaluatorModel = "claude-haiku-4.5" };

        Assert.Equal("gpt-5-mini", config.ModelFor(RoundPhase.Worker));
        Assert.Equal("claude-haiku-4.5", config.ModelFor(RoundPhase.Evaluator));
        Assert.Equal("high", config.ReasoningEffortFor(RoundPhase.Worker));
        Assert.Equal("", config.ReasoningEffortFor(RoundPhase.Evaluator)); // worker's "high" may not exist for this model

        config.EvaluatorReasoningEffort = "low";
        Assert.Equal("low", config.ReasoningEffortFor(RoundPhase.Evaluator));
    }

    [Fact]
    public async Task Usage_is_split_between_worker_and_evaluator()
    {
        var dir = Directory.CreateTempSubdirectory("loopeng-").FullName;
        try
        {
            var runner = new LoopRunner(new Factory());
            runner.Start(new LoopConfig
            {
                GoalName = "g", GoalDescription = "d", CriteriaText = "c", MaxLoops = 1, WorkingDirectories = [dir],
                Model = "gpt-5-mini", EvaluatorModel = "claude-haiku-4.5"
            });
            await runner.WaitForCompletionAsync();

            var round = runner.GetSnapshot().Rounds.Single();
            Assert.Equal(3, round.WorkerUsage.Models["gpt-5-mini"]);
            Assert.Equal(1, round.EvaluatorUsage.Models["claude-haiku-4.5"]);
            Assert.Equal(0.3, round.WorkerUsage.AiCredits!.Value, precision: 6);
            Assert.Equal(0.5, round.EvaluatorUsage.AiCredits!.Value, precision: 6);
            Assert.Equal(0.8, round.Usage.AiCredits!.Value, precision: 6);

            var progress = File.ReadAllText(runner.GetSnapshot().ProgressFilePath!);
            Assert.Contains("- **Evaluator:** GitHub Copilot, claude-haiku-4.5", progress);
            Assert.Contains("evaluator claude-haiku-4.5 ×1 (0.5 credits)", progress);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Reports usage under the model each phase is configured with, like the real runtime would.</summary>
    private sealed class Factory : ILoopAgentFactory
    {
        public ILoopAgent Create(LoopConfig config) => new Agent(config);

        private sealed class Agent(LoopConfig config) : ILoopAgent
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
            {
                var model = config.ModelFor(request.Phase);
                var calls = request.Phase == RoundPhase.Worker ? 3 : 1;
                for (var i = 0; i < calls; i++) observer.OnUsage(new UsageSample(100, 10, 0, 0, model));
                observer.OnBilling(0, 1, request.Phase == RoundPhase.Worker ? 0.3 : 0.5);
                return Task.FromResult(request.Phase == RoundPhase.Worker ? "done" : "{\"criteriaMet\": true}");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
