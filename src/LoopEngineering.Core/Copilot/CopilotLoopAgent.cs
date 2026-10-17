using GitHub.Copilot;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Copilot;

public sealed class CopilotLoopAgentFactory : ILoopAgentFactory
{
    public ILoopAgent Create(LoopConfig config) => new CopilotLoopAgent(config);
}

/// <summary>
/// <see cref="ILoopAgent"/> backed by the GitHub Copilot SDK. Each turn creates a new session
/// on a shared client so every round starts with a clean context.
/// </summary>
public sealed class CopilotLoopAgent(LoopConfig config) : ILoopAgent
{
    private CopilotClient? _client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client = await CopilotClients.StartAsync(config.TokenEnvironmentVariable, config.PrimaryWorkingDirectory, cancellationToken);
        // Fail before round 1 with GitHub's real reason instead of a generic authorization error per round.
        await CopilotClients.VerifyModelAccessAsync(_client, cancellationToken);
    }

    public async Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
    {
        if (_client is null) throw new InvalidOperationException("Agent not started.");

        await using var session = await _client.CreateSessionAsync(BuildSessionConfig(request.Phase), cancellationToken);

        var streamed = new System.Text.StringBuilder();
        using var subscription = session.On<SessionEvent>(evt => HandleEvent(evt, observer, streamed));

        // SendAndWaitAsync cancellation only stops waiting; abort the runtime work explicitly.
        await using var abortOnCancel = cancellationToken.Register(() => _ = AbortQuietly(session));

        try
        {
            var reply = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = request.Prompt }, request.Timeout, cancellationToken);

            var content = reply?.Data.Content;
            return string.IsNullOrWhiteSpace(content) ? streamed.ToString() : content;
        }
        finally
        {
            // Also on stop / timeout: a cancelled turn was still billed for its prompt.
            await ReportBillingAsync(session, observer);
        }
    }

    /// <summary>
    /// Reads the session's billed premium requests from the runtime. GitHub bills per user prompt
    /// (× model multiplier), not per API call, so summing per-call usage events would overcount.
    /// </summary>
    private static async Task ReportBillingAsync(CopilotSession session, IAgentObserver observer)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var metrics = await session.Rpc.Usage.GetMetricsAsync(timeout.Token);
            // Credit-billed accounts are charged in AI credits; the runtime reports them in nano-AIU.
            double? credits = metrics.TotalNanoAiu is { } nano && metrics.AiCreditsStatus != AiCreditsStatus.Unavailable
                ? nano / 1_000_000_000d
                : null;
            observer.OnBilling(metrics.TotalPremiumRequestCost, metrics.TotalUserRequests, credits);
        }
        catch (Exception ex)
        {
            observer.OnLog("warn", $"Could not read premium request usage: {ex.Message}");
        }
    }

    private SessionConfig BuildSessionConfig(RoundPhase phase)
    {
        var sessionConfig = new SessionConfig
        {
            WorkingDirectory = config.PrimaryWorkingDirectory,
            AdditionalDirectories = config.WorkingDirectories.Count > 1 ? [.. config.WorkingDirectories.Skip(1)] : null,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Streaming = true,
            IncludeSubAgentStreamingEvents = false,
            EnableConfigDiscovery = config.EnableConfigDiscovery,
            SkillDirectories = config.SkillDirectories.Count > 0 ? [.. config.SkillDirectories] : null,
            AgentDirectories = config.AgentDirectories.Count > 0 ? [.. config.AgentDirectories] : null,
            CustomAgents = config.CustomAgents.Count > 0
                ? config.CustomAgents.Select(a => new CustomAgentConfig
                {
                    Name = a.Name,
                    DisplayName = a.Name,
                    Description = a.Description,
                    Prompt = a.Prompt,
                    Infer = true,
                    Skills = a.Skills.Count > 0 ? [.. a.Skills] : null
                }).ToList()
                : null,
            // The evaluator always runs as the default agent so a custom persona cannot grade its own work.
            Agent = phase == RoundPhase.Worker && !string.IsNullOrWhiteSpace(config.ActiveAgent) ? config.ActiveAgent : null
        };

        if (!string.IsNullOrWhiteSpace(config.Model)) sessionConfig.Model = config.Model;
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort)) sessionConfig.ReasoningEffort = config.ReasoningEffort;
        return sessionConfig;
    }

    private static void HandleEvent(SessionEvent evt, IAgentObserver observer, System.Text.StringBuilder streamed)
    {
        switch (evt)
        {
            // Only stream the top-level agent's text; sub-agent events carry an AgentId.
            case AssistantMessageDeltaEvent delta when delta.AgentId is null:
                streamed.Append(delta.Data.DeltaContent);
                observer.OnOutputDelta(delta.Data.DeltaContent ?? "");
                break;
            case ToolExecutionStartEvent tool when tool.AgentId is null:
                observer.OnToolStarted(tool.Data.ToolName ?? "tool", tool.Data.ToolTitle);
                break;
            case SubagentStartedEvent started:
                observer.OnSubagentStarted(
                    started.Data.ToolCallId ?? Guid.NewGuid().ToString("N"),
                    started.Data.AgentDisplayName ?? started.Data.AgentName ?? "subagent",
                    started.Data.AgentDescription);
                break;
            case SubagentCompletedEvent completed:
                observer.OnSubagentFinished(completed.Data.ToolCallId ?? "", true, null);
                break;
            case SubagentFailedEvent failed:
                observer.OnSubagentFinished(failed.Data.ToolCallId ?? "", false, failed.Data.Error);
                break;
            // Counted for every agent, sub-agents included: they all spend tokens.
            case AssistantUsageEvent usage:
                observer.OnUsage(new UsageSample(
                    usage.Data.InputTokens ?? 0,
                    usage.Data.OutputTokens ?? 0,
                    usage.Data.CacheReadTokens ?? 0,
                    usage.Data.ReasoningTokens ?? 0,
                    usage.Data.Model));
                break;
            case SessionErrorEvent error:
                observer.OnLog("error", error.Data.Message ?? "Session error");
                break;
        }
    }

    private static async Task AbortQuietly(CopilotSession session)
    {
        try { await session.AbortAsync(); }
        catch { /* session may already be closed */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        await CopilotClients.StopQuietlyAsync(_client);
        _client = null;
    }
}
