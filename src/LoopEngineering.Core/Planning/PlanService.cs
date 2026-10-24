using System.Text.Json;
using System.Text.Json.Serialization;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Issues;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Planning;

/// <summary>
/// Runs the planner before a loop, keeps the resulting plan while the user reviews, edits and approves it,
/// and persists it so a page reload does not lose it. Register as a singleton.
/// </summary>
public sealed class PlanService(string storePath, ILoopAgentFactory agentFactory, IGitHubIssueFetcher? issueFetcher = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly object _gate = new();
    private LoopPlan? _plan = Load(storePath);

    public event Action? Changed;

    /// <summary>Usage of each planner run, for the credit ledger.</summary>
    public event Action<TokenUsage>? UsageRecorded;

    public bool IsPlanning { get; private set; }
    public string? Error { get; private set; }
    public TokenUsage? LastUsage { get; private set; }

    public LoopPlan? Plan
    {
        get { lock (_gate) return _plan?.Clone(); }
    }

    /// <summary>True when the stored plan was made for the current goal, criteria and folders.</summary>
    public bool IsCurrentFor(LoopConfig config)
    {
        lock (_gate) return _plan is not null && _plan.InputFingerprint == LoopPlan.Fingerprint(config);
    }

    public bool IsApprovedFor(LoopConfig config)
    {
        lock (_gate) return IsCurrentFor(config) && _plan!.Approved;
    }

    /// <summary>Runs the planner once. Never throws; failures land in <see cref="Error"/>.</summary>
    public async Task PlanAsync(LoopConfig config, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (IsPlanning) return;
            IsPlanning = true;
            Error = null;
        }
        Notify();

        var usage = new TokenUsage();
        try
        {
            var goalResolver = new GoalResolver(config, issueFetcher);
            var goal = await goalResolver.ResolveAsync(cancellationToken);
            var criteria = config.CriteriaSource == CriteriaSource.File ? await File.ReadAllTextAsync(config.CriteriaFilePath, cancellationToken) : config.CriteriaText;
            var guides = config.UseAgentsMd ? config.WorkingDirectories.Select(RepoGuide.Read).ToList() : [];
            var prompt = PromptBuilder.BuildPlannerPrompt(config, goal, criteria, guides);

            // Run the planner on its own backend only: the loop's backends are not needed for this.
            var backend = config.BackendFor(RoundPhase.Planner);
            var probe = config.Clone();
            probe.PlannerModel = config.ModelFor(RoundPhase.Planner);
            probe.PlannerReasoningEffort = config.ReasoningEffortFor(RoundPhase.Planner);
            probe.PlannerBackend = backend switch
            {
                AgentBackend.ClaudeCode => HelperBackendChoice.ClaudeCode,
                AgentBackend.Antigravity => HelperBackendChoice.Antigravity,
                _ => HelperBackendChoice.Copilot
            };
            probe.WorkerBackend = backend;
            probe.EvaluatorBackend = backend;
            probe.UseAgentsMd = false;

            await using var agent = agentFactory.Create(probe);
            await agent.StartAsync(cancellationToken);
            var reply = await agent.RunTurnAsync(
                new AgentTurnRequest(RoundPhase.Planner, prompt, TimeSpan.FromMinutes(Math.Max(5, config.TurnTimeoutMinutes))),
                new UsageObserver(usage), cancellationToken);

            var plan = PlanParser.Parse(reply, goal, criteria);
            plan.InputFingerprint = LoopPlan.Fingerprint(config);
            lock (_gate) _plan = plan;
            Save();
        }
        catch (Exception ex)
        {
            lock (_gate) Error = ex.Message;
        }
        finally
        {
            lock (_gate)
            {
                IsPlanning = false;
                LastUsage = usage;
            }
            try { UsageRecorded?.Invoke(usage.Clone()); } catch { /* ledger errors are not fatal */ }
            Notify();
        }
    }

    /// <summary>Replaces the plan with the user's edits (tasks, criteria). Editing clears an approval.</summary>
    public void Update(LoopPlan edited)
    {
        lock (_gate)
        {
            if (_plan is null) return;
            var copy = edited.Clone();
            copy.InputFingerprint = _plan.InputFingerprint;
            copy.Approved = false;
            copy.Tasks.RemoveAll(t => string.IsNullOrWhiteSpace(t.Title) && string.IsNullOrWhiteSpace(t.Criteria));
            _plan = copy;
        }
        Save();
        Notify();
    }

    public void Approve()
    {
        lock (_gate)
        {
            if (_plan is null || _plan.Tasks.Count == 0) return;
            _plan.Approved = true;
        }
        Save();
        Notify();
    }

    public void Discard()
    {
        lock (_gate)
        {
            _plan = null;
            Error = null;
        }
        try { File.Delete(storePath); } catch (IOException) { }
        Notify();
    }

    private void Save()
    {
        try
        {
            LoopPlan? plan;
            lock (_gate) plan = _plan?.Clone();
            if (plan is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            File.WriteAllText(storePath, JsonSerializer.Serialize(plan, JsonOptions));
        }
        catch (IOException)
        {
            // the plan still works for this session
        }
    }

    private static LoopPlan? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<LoopPlan>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* UI subscriber errors are not ours */ }
    }

    private sealed class UsageObserver(TokenUsage usage) : IAgentObserver
    {
        public void OnOutputDelta(string text) { }
        public void OnToolStarted(string toolName, string? detail) { }
        public void OnSubagentStarted(string id, string name, string? description) { }
        public void OnSubagentFinished(string id, bool succeeded, string? error) { }
        public void OnUsage(UsageSample sample) => usage.Add(sample);
        public void OnBilling(double premiumRequests, long userRequests, double? aiCredits) => usage.AddBilling(premiumRequests, userRequests, aiCredits);
        public void OnEstimatedCost(double usd) => usage.AddEstimatedCost(usd);
        public void OnLog(string level, string message) { }
    }
}
