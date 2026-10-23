using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Agents;

/// <summary>
/// Runs single prompt turns against an LLM agent backend. One instance lives for one loop run.
/// Every turn should start from a fresh context; state carries over through the working directory
/// and the progress file only.
/// </summary>
public interface ILoopAgent : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken);
}

/// <param name="WorkingDirectory">Overrides the session's working directory (e.g. the folder a guide is written for).</param>
public sealed record AgentTurnRequest(RoundPhase Phase, string Prompt, TimeSpan Timeout, string? WorkingDirectory = null);

/// <summary>Receives live activity from a running turn.</summary>
public interface IAgentObserver
{
    void OnOutputDelta(string text);
    void OnToolStarted(string toolName, string? detail);
    void OnSubagentStarted(string id, string name, string? description);
    void OnSubagentFinished(string id, bool succeeded, string? error);
    void OnUsage(UsageSample usage);

    /// <summary>Billing for the finished turn as reported by the runtime: premium requests, prompts, and AI credits (if known).</summary>
    void OnBilling(double premiumRequests, long userRequests, double? aiCredits);

    /// <summary>Claude Code's reported cost for the finished turn (USD at API prices).</summary>
    void OnEstimatedCost(double usd);
    void OnLog(string level, string message);
}

public interface ILoopAgentFactory
{
    ILoopAgent Create(LoopConfig config);
}
