using LoopEngineering.Core.Loop;

namespace LoopEngineering.Tests;

public sealed class CreditLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Accumulates_and_survives_restart()
    {
        var now = new DateTimeOffset(2026, 10, 17, 4, 0, 0, TimeSpan.Zero);
        new CreditLedger(_path, () => now).Add(13.1);
        var reloaded = new CreditLedger(_path, () => now);
        reloaded.Add(0.4);

        Assert.Equal(13.5, reloaded.CurrentMonthCredits, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), reloaded.NextReset);
    }

    [Fact]
    public void Resets_in_a_new_month()
    {
        var now = new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero);
        var ledger = new CreditLedger(_path, () => now);
        ledger.Add(5);

        now = now.AddHours(2); // Nov 1 UTC
        Assert.Equal(0, ledger.CurrentMonthCredits);
        ledger.Add(1);
        Assert.Equal(1, ledger.CurrentMonthCredits);
    }

    [Fact]
    public async Task Runner_reports_each_rounds_usage()
    {
        var dir = Directory.CreateTempSubdirectory("loopeng-").FullName;
        try
        {
            var runner = new LoopRunner(new SingleTurnFactory());
            var reported = new List<double?>();
            runner.RoundUsageRecorded += u => reported.Add(u.AiCredits);

            runner.Start(new() { GoalName = "g", GoalDescription = "d", CriteriaText = "c", MaxLoops = 1, WorkingDirectories = [dir], UseAgentsMd = false });
            await runner.WaitForCompletionAsync();

            Assert.Equal([0.75], reported);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SingleTurnFactory : LoopEngineering.Core.Agents.ILoopAgentFactory
    {
        public LoopEngineering.Core.Agents.ILoopAgent Create(LoopEngineering.Core.Models.LoopConfig config) => new Agent();

        private sealed class Agent : LoopEngineering.Core.Agents.ILoopAgent
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<string> RunTurnAsync(LoopEngineering.Core.Agents.AgentTurnRequest request,
                LoopEngineering.Core.Agents.IAgentObserver observer, CancellationToken cancellationToken)
            {
                observer.OnBilling(1, 1, request.Phase == LoopEngineering.Core.Models.RoundPhase.Worker ? 0.5 : 0.25);
                return Task.FromResult(request.Phase == LoopEngineering.Core.Models.RoundPhase.Worker ? "done" : "{\"criteriaMet\": true}");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
