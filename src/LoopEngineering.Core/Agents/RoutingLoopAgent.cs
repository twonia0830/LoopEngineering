using LoopEngineering.Core.Antigravity;
using LoopEngineering.Core.ClaudeCode;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Agents;

/// <summary>Creates a <see cref="RoutingLoopAgent"/> that sends each role to the backend configured for it.</summary>
public sealed class LoopAgentFactory : ILoopAgentFactory
{
    public ILoopAgent Create(LoopConfig config) => new RoutingLoopAgent(config, new Dictionary<AgentBackend, Func<ILoopAgent>>
    {
        [AgentBackend.Copilot] = () => new CopilotLoopAgent(config),
        [AgentBackend.ClaudeCode] = () => new ClaudeCodeAgent(config),
        [AgentBackend.Antigravity] = () => new AntigravityAgent(config)
    });
}

/// <summary>
/// Routes each turn to the backend its role uses (e.g. worker on Copilot, evaluator on Claude Code).
/// Only the backends the configuration actually uses are started.
/// </summary>
public sealed class RoutingLoopAgent(LoopConfig config, IReadOnlyDictionary<AgentBackend, Func<ILoopAgent>> backends) : ILoopAgent
{
    private readonly Dictionary<AgentBackend, ILoopAgent> _started = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var backend in Enum.GetValues<AgentBackend>().Where(config.UsesBackend))
        {
            var agent = backends[backend]();
            _started[backend] = agent; // registered first so DisposeAsync cleans up even if start fails
            await agent.StartAsync(cancellationToken);
        }
    }

    public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
    {
        var backend = config.BackendFor(request.Phase);
        if (!_started.TryGetValue(backend, out var agent))
            throw new InvalidOperationException($"The {backend} backend was not started for the {request.Phase} role.");
        return agent.RunTurnAsync(request, observer, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _started.Values) await agent.DisposeAsync();
        _started.Clear();
    }
}
