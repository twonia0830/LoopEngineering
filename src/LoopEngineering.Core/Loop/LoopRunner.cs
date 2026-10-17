using System.Text;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Issues;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

/// <summary>
/// Runs one loop at a time: worker turn → evaluator turn → progress file, until the criteria
/// are met, max loops is reached, or the user stops it. Register as a singleton.
/// </summary>
public sealed class LoopRunner
{
    private const int MaxLiveOutputChars = 60_000;
    private const int MaxLogEntries = 500;

    private readonly ILoopAgentFactory _agentFactory;
    private readonly IGitHubIssueFetcher? _issueFetcher;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private RunStatus _status = RunStatus.Idle;
    private RoundPhase _phase;
    private int _currentRound;
    private LoopConfig? _config;
    private string? _progressPath;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private string? _activity;
    private readonly StringBuilder _liveOutput = new();
    private readonly List<RoundResult> _rounds = [];
    private readonly List<LogEntry> _log = [];

    public LoopRunner(ILoopAgentFactory agentFactory, IGitHubIssueFetcher? issueFetcher = null)
    {
        _agentFactory = agentFactory;
        _issueFetcher = issueFetcher;
    }

    /// <summary>Raised (on a background thread) whenever state changes.</summary>
    public event Action? Changed;

    /// <summary>Raised after each round is recorded and when a run ends; a good moment to refresh account quota.</summary>
    public event Action? RoundCompleted;

    /// <summary>Raised once per recorded round (including stopped or failed ones) with that round's usage.</summary>
    public event Action<TokenUsage>? RoundUsageRecorded;

    public LoopSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new LoopSnapshot
            {
                Status = _status,
                Phase = _phase,
                CurrentRound = _currentRound,
                MaxLoops = _config?.MaxLoops ?? 0,
                GoalName = _config?.GoalName ?? "",
                ProgressFilePath = _progressPath,
                StartedAt = _startedAt,
                EndedAt = _endedAt,
                LiveOutput = _liveOutput.ToString(),
                CurrentActivity = _activity,
                Rounds = _rounds.Select(r => r.Clone()).ToList(),
                ActiveSubagents = _rounds.LastOrDefault()?.Subagents.Where(s => s.IsRunning).Select(s => s.Clone()).ToList() ?? [],
                Log = _log.ToList(),
                TotalUsage = TokenUsage.Sum(_rounds.Select(r => r.Usage))
            };
        }
    }

    /// <summary>Starts a run in the background. Throws if one is already active or the config is invalid.</summary>
    public void Start(LoopConfig config)
    {
        var errors = config.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));

        lock (_gate)
        {
            if (_status is RunStatus.Running or RunStatus.Stopping)
                throw new InvalidOperationException("A loop is already running.");

            _config = config.Clone();
            _status = RunStatus.Running;
            _phase = RoundPhase.None;
            _currentRound = 0;
            _startedAt = DateTimeOffset.Now;
            _endedAt = null;
            _activity = "Starting Copilot runtime…";
            _liveOutput.Clear();
            _rounds.Clear();
            _log.Clear();
            _progressPath = new ProgressFile(_config.EffectiveProgressDirectory, _config.GoalName).Path;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var runConfig = _config;
            _runTask = Task.Run(() => RunAsync(runConfig, token));
        }
        Notify();
    }

    /// <summary>Requests a stop. The current turn is aborted and the run ends as Stopped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_status != RunStatus.Running) return;
            _status = RunStatus.Stopping;
            _activity = "Stopping…";
            _cts?.Cancel();
        }
        AddLog("info", "Stop requested.");
    }

    /// <summary>Completes when the current run (if any) has fully finished.</summary>
    public Task WaitForCompletionAsync() => _runTask ?? Task.CompletedTask;

    private async Task RunAsync(LoopConfig config, CancellationToken ct)
    {
        var progress = new ProgressFile(config.EffectiveProgressDirectory, config.GoalName);
        var goal = new GoalResolver(config, _issueFetcher);
        var finalStatus = RunStatus.MaxLoopsReached;
        var consecutiveFailures = 0;

        try
        {
            if (config.GoalSource == GoalSource.GitHubIssues) SetActivity("Fetching GitHub issues…");
            // Resolve up front so a bad link or unreadable issue fails the run before any credits are spent.
            var goalText = await goal.ResolveAsync(ct);
            progress.WriteRunHeader(config, goalText, goal.SourceDescription, ReadCriteria(config), _startedAt ?? DateTimeOffset.Now);
            AddLog("info", $"Progress file: {progress.Path}");

            await using var agent = _agentFactory.Create(config);
            await agent.StartAsync(ct);
            AddLog("info", "Copilot runtime started.");

            for (var round = 1; round <= config.MaxLoops; round++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await RunRoundAsync(agent, config, goal, progress, round, ct);

                if (result.Status == RoundStatus.Failed)
                {
                    if (++consecutiveFailures >= config.MaxConsecutiveFailures)
                    {
                        AddLog("error", $"{consecutiveFailures} rounds failed in a row; giving up.");
                        finalStatus = RunStatus.Failed;
                        break;
                    }
                    continue;
                }

                consecutiveFailures = 0;
                if (result.Evaluation?.CriteriaMet == true)
                {
                    finalStatus = RunStatus.CriteriaMet;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            finalStatus = RunStatus.Stopped;
        }
        catch (Exception ex)
        {
            AddLog("error", ex.Message);
            finalStatus = RunStatus.Failed;
        }

        // A stop that lands while the last round is being recorded still counts as a stop.
        if (ct.IsCancellationRequested && finalStatus != RunStatus.CriteriaMet) finalStatus = RunStatus.Stopped;

        var endedAt = DateTimeOffset.Now;
        int roundsRun;
        lock (_gate)
        {
            _status = finalStatus;
            _phase = RoundPhase.None;
            _endedAt = endedAt;
            _activity = null;
            roundsRun = _rounds.Count;
        }

        TokenUsage total;
        lock (_gate) total = TokenUsage.Sum(_rounds.Select(r => r.Usage));

        try { progress.AppendFinal(finalStatus, roundsRun, endedAt, total); }
        catch (Exception ex) { AddLog("error", $"Could not write progress file: {ex.Message}"); }

        AddLog("info", $"Run finished: {finalStatus}. Tokens: {total}.");
        RaiseRoundCompleted();
    }

    private async Task<RoundResult> RunRoundAsync(ILoopAgent agent, LoopConfig config, GoalResolver goal, ProgressFile progress, int number, CancellationToken ct)
    {
        var result = new RoundResult { Number = number, StartedAt = DateTimeOffset.Now };
        lock (_gate)
        {
            _currentRound = number;
            _rounds.Add(result);
            _liveOutput.Clear();
        }
        AddLog("info", $"Round {number} started.");

        var timeout = TimeSpan.FromMinutes(config.TurnTimeoutMinutes);
        try
        {
            // Re-read every round so the criteria file can be edited while the loop runs.
            var criteria = ReadCriteria(config);
            var goalText = await goal.ResolveAsync(ct);

            SetPhase(RoundPhase.Worker, "Worker is running…");
            var workerPrompt = PromptBuilder.BuildWorkerPrompt(config, goalText, criteria, number, progress.Path, progress.Read());
            var workerOutput = await agent.RunTurnAsync(
                new AgentTurnRequest(RoundPhase.Worker, workerPrompt, timeout), new Observer(this, result, RoundPhase.Worker), ct);
            lock (_gate) result.WorkerOutput = workerOutput;

            SetPhase(RoundPhase.Evaluator, "Evaluator is checking the criteria…");
            AppendLive("\n\n---- evaluator ----\n\n");
            var evalPrompt = PromptBuilder.BuildEvaluatorPrompt(config, goalText, criteria, number, workerOutput);
            var evalOutput = await agent.RunTurnAsync(
                new AgentTurnRequest(RoundPhase.Evaluator, evalPrompt, timeout), new Observer(this, result, RoundPhase.Evaluator), ct);
            var evaluation = EvaluationParser.Parse(evalOutput);

            lock (_gate)
            {
                result.Evaluation = evaluation;
                result.Status = RoundStatus.Completed;
            }
            AddLog(evaluation.CriteriaMet ? "success" : "info",
                $"Round {number}: {(evaluation.CriteriaMet ? "criteria met" : "criteria not met")}{(evaluation.Score is int s ? $" (score {s})" : "")}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_gate) result.Status = RoundStatus.Cancelled;
            FinishRound(result, progress, config);
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                result.Status = RoundStatus.Failed;
                result.Error = ex.Message;
            }
            AddLog("error", $"Round {number} failed: {ex.Message}");
        }

        FinishRound(result, progress, config);
        return result;
    }

    private void FinishRound(RoundResult result, ProgressFile progress, LoopConfig config)
    {
        RoundResult copy;
        lock (_gate)
        {
            result.EndedAt = DateTimeOffset.Now;
            foreach (var running in result.Subagents.Where(s => s.IsRunning))
            {
                running.EndedAt = result.EndedAt;
                running.Succeeded = false;
                running.Error = "Round ended";
            }
            copy = result.Clone();
        }

        try { progress.AppendRound(copy, config.MaxLoops); }
        catch (Exception ex) { AddLog("error", $"Could not write progress file: {ex.Message}"); }
        Notify();

        try { RoundUsageRecorded?.Invoke(copy.Usage); }
        catch (Exception ex) { AddLog("error", $"Could not record usage: {ex.Message}"); }

        RaiseRoundCompleted();
    }

    private void RaiseRoundCompleted()
    {
        try { RoundCompleted?.Invoke(); }
        catch { /* subscribers must not kill the loop */ }
    }

    private static string ReadCriteria(LoopConfig config) =>
        config.CriteriaSource == CriteriaSource.File ? File.ReadAllText(config.CriteriaFilePath) : config.CriteriaText;

    private void SetActivity(string activity)
    {
        lock (_gate) _activity = activity;
        Notify();
    }

    private void SetPhase(RoundPhase phase, string activity)
    {
        lock (_gate)
        {
            _phase = phase;
            _activity = activity;
        }
        Notify();
    }

    private void AppendLive(string text)
    {
        lock (_gate)
        {
            _liveOutput.Append(text);
            if (_liveOutput.Length > MaxLiveOutputChars)
                _liveOutput.Remove(0, _liveOutput.Length - MaxLiveOutputChars);
        }
        Notify();
    }

    private void AddLog(string level, string message)
    {
        lock (_gate)
        {
            _log.Add(new LogEntry(DateTimeOffset.Now, level, message));
            if (_log.Count > MaxLogEntries) _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }
        Notify();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* a broken UI subscriber must not kill the loop */ }
    }

    private sealed class Observer(LoopRunner runner, RoundResult round, RoundPhase phase) : IAgentObserver
    {
        public void OnOutputDelta(string text) => runner.AppendLive(text);

        public void OnToolStarted(string toolName, string? detail)
        {
            lock (runner._gate)
            {
                round.ToolCalls++;
                runner._activity = $"{phase}: {detail ?? toolName}";
            }
            runner.Notify();
        }

        public void OnSubagentStarted(string id, string name, string? description)
        {
            lock (runner._gate)
            {
                round.Subagents.Add(new SubagentRecord
                {
                    Id = id,
                    Name = name,
                    Description = description ?? "",
                    Phase = phase,
                    StartedAt = DateTimeOffset.Now
                });
            }
            runner.AddLog("info", $"Sub-agent started: {name}");
        }

        public void OnSubagentFinished(string id, bool succeeded, string? error)
        {
            string? name;
            lock (runner._gate)
            {
                var record = round.Subagents.LastOrDefault(s => s.Id == id && s.IsRunning);
                if (record is null) return;
                record.EndedAt = DateTimeOffset.Now;
                record.Succeeded = succeeded;
                record.Error = error;
                name = record.Name;
            }
            runner.AddLog(succeeded ? "info" : "error", $"Sub-agent {(succeeded ? "finished" : "failed")}: {name}{(error is null ? "" : $" — {error}")}");
        }

        public void OnUsage(UsageSample usage)
        {
            lock (runner._gate) round.Usage.Add(usage);
            runner.Notify();
        }

        public void OnBilling(double premiumRequests, long userRequests, double? aiCredits)
        {
            lock (runner._gate) round.Usage.AddBilling(premiumRequests, userRequests, aiCredits);
            runner.Notify();
        }

        public void OnLog(string level, string message) => runner.AddLog(level, message);
    }
}
