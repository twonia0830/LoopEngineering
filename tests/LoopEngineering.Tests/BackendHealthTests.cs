using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Antigravity;
using LoopEngineering.Core.Health;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class BackendHealthTests : IDisposable
{
    private const string TokenVar = "LOOP_HEALTH_TEST_TOKEN";
    private readonly string _dir = Directory.CreateTempSubdirectory("loopeng-health-").FullName;
    private string StorePath => Path.Combine(_dir, "verification.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVar, null);
        Directory.Delete(_dir, recursive: true);
    }

    private LoopConfig Settings() => new() { TokenEnvironmentVariable = TokenVar, ClaudeTokenEnvironmentVariable = TokenVar };

    private BackendHealthService Service(FakeAgent agent) => new(
        StorePath,
        new ModelCatalogService(new FakeModels()),
        new AntigravityModelCatalog(),
        (_, config) => { agent.Config = config; return agent; });

    [Fact]
    public async Task Unset_token_means_not_configured_and_verify_does_nothing()
    {
        var agent = new FakeAgent("OK");
        var health = Service(agent);

        await health.VerifyAsync(AgentBackend.Copilot, Settings());

        var status = health.Get(AgentBackend.Copilot);
        Assert.False(status.IsConfigured);
        Assert.False(status.IsVerified);
        Assert.Contains(TokenVar, status.SetupDetail);
        Assert.Equal(0, agent.Turns);
    }

    [Fact]
    public async Task Verification_is_remembered_until_the_token_changes()
    {
        Environment.SetEnvironmentVariable(TokenVar, "token-one");
        var agent = new FakeAgent("OK");

        await Service(agent).VerifyAsync(AgentBackend.Copilot, Settings());
        Assert.Equal(1, agent.Turns);
        Assert.Equal("cheap-model", agent.Config!.ModelFor(RoundPhase.Evaluator)); // cheapest priced model, not auto
        Assert.DoesNotContain("token-one", File.ReadAllText(StorePath));          // only a fingerprint is stored

        // A new app run (new service instance) still sees the verification.
        var restarted = Service(agent);
        await restarted.CheckAsync(AgentBackend.Copilot, Settings());
        Assert.True(restarted.Get(AgentBackend.Copilot).IsUsable);
        Assert.Equal("cheap-model", restarted.Get(AgentBackend.Copilot).VerifiedModel);

        // A different token means it has to be verified again.
        Environment.SetEnvironmentVariable(TokenVar, "token-two");
        await restarted.CheckAsync(AgentBackend.Copilot, Settings());
        var status = restarted.Get(AgentBackend.Copilot);
        Assert.True(status.IsConfigured);
        Assert.False(status.IsVerified);
    }

    [Fact]
    public async Task Failed_verification_shows_the_error_and_forgets_old_success()
    {
        Environment.SetEnvironmentVariable(TokenVar, "token-one");
        await Service(new FakeAgent("OK")).VerifyAsync(AgentBackend.Copilot, Settings());

        var failing = Service(new FakeAgent(throwMessage: "Authorization error"));
        await failing.VerifyAsync(AgentBackend.Copilot, Settings());

        var status = failing.Get(AgentBackend.Copilot);
        Assert.False(status.IsVerified);
        Assert.Equal("Authorization error", status.LastError);

        var again = Service(new FakeAgent("OK"));
        await again.CheckAsync(AgentBackend.Copilot, Settings());
        Assert.False(again.Get(AgentBackend.Copilot).IsVerified);
    }

    [Fact]
    public async Task Unexpected_reply_does_not_verify()
    {
        Environment.SetEnvironmentVariable(TokenVar, "token-one");
        var health = Service(new FakeAgent("I cannot help with that."));
        await health.VerifyAsync(AgentBackend.Copilot, Settings());
        Assert.False(health.Get(AgentBackend.Copilot).IsVerified);
        Assert.Contains("Unexpected reply", health.Get(AgentBackend.Copilot).LastError);
    }

    [Fact]
    public async Task Claude_needs_both_the_token_and_the_cli()
    {
        Environment.SetEnvironmentVariable(TokenVar, "sk-ant-oat01-test");
        var settings = Settings();
        settings.ClaudeCliPath = Path.Combine(_dir, "missing-claude.exe");

        var health = Service(new FakeAgent("OK"));
        await health.CheckAsync(AgentBackend.ClaudeCode, settings);
        Assert.False(health.Get(AgentBackend.ClaudeCode).IsConfigured);

        var fakeCli = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(fakeCli, "");
        settings.ClaudeCliPath = fakeCli;
        await health.VerifyAsync(AgentBackend.ClaudeCode, settings);

        var status = health.Get(AgentBackend.ClaudeCode);
        Assert.True(status.IsUsable);
        Assert.Equal("claude-haiku-5-5", status.VerifiedModel); // cheapest Claude model
    }

    private sealed class FakeModels : IModelCatalogSource
    {
        public Task<IReadOnlyList<ModelOption>> ListModelsAsync(string tokenVariable, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelOption>>(
            [
                new("auto", "Auto", null, [], null, null, null, null),
                new("pricey-model", "Pricey", "enabled", [], null, 200, 20, 1000),
                new("cheap-model", "Cheap", "enabled", [], null, 10, 1, 50),
                new("blocked-model", "Blocked", "disabled", [], null, 1, 0.1, 5)
            ]);
    }

    private sealed class FakeAgent(string? reply = null, string? throwMessage = null) : ILoopAgent
    {
        public int Turns { get; private set; }
        public LoopConfig? Config { get; set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
        {
            Turns++;
            if (throwMessage is not null) throw new InvalidOperationException(throwMessage);
            return Task.FromResult(reply ?? "");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
