namespace LoopEngineering.Core.Models;

public enum RunStatus
{
    Idle,
    Running,
    Stopping,
    CriteriaMet,
    MaxLoopsReached,
    Stopped,
    Failed
}

public enum RoundPhase
{
    None,
    Worker,
    Evaluator
}

public enum RoundStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed class Evaluation
{
    public bool CriteriaMet { get; init; }
    public int? Score { get; init; }
    public string Reasoning { get; init; } = "";
    public string NextSteps { get; init; } = "";
    public string Raw { get; init; } = "";
}

public sealed class SubagentRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public RoundPhase Phase { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public bool? Succeeded { get; set; }
    public string? Error { get; set; }

    public bool IsRunning => EndedAt is null;

    public SubagentRecord Clone() => (SubagentRecord)MemberwiseClone();
}

public sealed class RoundResult
{
    public int Number { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public RoundStatus Status { get; set; } = RoundStatus.Running;
    public string WorkerOutput { get; set; } = "";
    public Evaluation? Evaluation { get; set; }
    public List<SubagentRecord> Subagents { get; set; } = [];
    public int ToolCalls { get; set; }
    public TokenUsage Usage { get; set; } = new();
    public string? Error { get; set; }

    public TimeSpan? Duration => EndedAt - StartedAt;

    public RoundResult Clone()
    {
        var copy = (RoundResult)MemberwiseClone();
        copy.Subagents = Subagents.Select(s => s.Clone()).ToList();
        copy.Usage = Usage.Clone();
        return copy;
    }
}

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

/// <summary>Immutable copy of the runner state handed to the UI.</summary>
public sealed class LoopSnapshot
{
    public RunStatus Status { get; init; }
    public RoundPhase Phase { get; init; }
    public int CurrentRound { get; init; }
    public int MaxLoops { get; init; }
    public string GoalName { get; init; } = "";
    public string? ProgressFilePath { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string LiveOutput { get; init; } = "";
    public string? CurrentActivity { get; init; }
    public IReadOnlyList<RoundResult> Rounds { get; init; } = [];
    public IReadOnlyList<SubagentRecord> ActiveSubagents { get; init; } = [];
    public IReadOnlyList<LogEntry> Log { get; init; } = [];
    public TokenUsage TotalUsage { get; init; } = new();

    public bool IsActive => Status is RunStatus.Running or RunStatus.Stopping;
}
