using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Planning;

public enum PlanIssueSeverity
{
    /// <summary>The loop would likely waste rounds until this is fixed.</summary>
    Blocker,
    Warning,
    Info
}

public enum PlanTaskStatus
{
    Pending,
    Passed
}

public sealed class PlanIssue
{
    public PlanIssueSeverity Severity { get; set; } = PlanIssueSeverity.Warning;
    public string Message { get; set; } = "";
    public string Suggestion { get; set; } = "";
}

public sealed class PlanTask
{
    public string Title { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Criteria { get; set; } = "";
    public PlanTaskStatus Status { get; set; } = PlanTaskStatus.Pending;
    public int Attempts { get; set; }

    public PlanTask Clone() => (PlanTask)MemberwiseClone();
}

/// <summary>A command the planner suggests to check a criterion deterministically. Shown only; never run automatically.</summary>
public sealed class SuggestedCheck
{
    public string Command { get; set; } = "";
    public string Verifies { get; set; } = "";
}

/// <summary>
/// The planner's review of a goal: concerns about the goal and criteria, an ordered task list (a single task
/// for small goals), and suggested deterministic checks. Approved by the user before the loop starts.
/// </summary>
public sealed class LoopPlan
{
    public string Summary { get; set; } = "";
    public List<PlanIssue> Issues { get; set; } = [];
    public List<PlanTask> Tasks { get; set; } = [];
    public List<SuggestedCheck> SuggestedChecks { get; set; } = [];

    /// <summary>Fingerprint of the goal and criteria the plan was made for; a changed goal needs a new plan.</summary>
    public string InputFingerprint { get; set; } = "";

    public bool Approved { get; set; }

    [JsonIgnore]
    public bool HasBlockers => Issues.Any(i => i.Severity == PlanIssueSeverity.Blocker);

    [JsonIgnore]
    public PlanTask? CurrentTask => Tasks.FirstOrDefault(t => t.Status != PlanTaskStatus.Passed);

    [JsonIgnore]
    public bool AllTasksPassed => Tasks.Count > 0 && Tasks.All(t => t.Status == PlanTaskStatus.Passed);

    public LoopPlan Clone() => new()
    {
        Summary = Summary,
        Issues = Issues.Select(i => new PlanIssue { Severity = i.Severity, Message = i.Message, Suggestion = i.Suggestion }).ToList(),
        Tasks = Tasks.Select(t => t.Clone()).ToList(),
        SuggestedChecks = SuggestedChecks.Select(c => new SuggestedCheck { Command = c.Command, Verifies = c.Verifies }).ToList(),
        InputFingerprint = InputFingerprint,
        Approved = Approved
    };

    /// <summary>Hash of what the plan depends on: goal source/content and criteria.</summary>
    public static string Fingerprint(LoopConfig config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f",
            config.GoalSource, config.GoalDescription, config.GoalFilePath, config.GoalIssueLinks,
            config.CriteriaSource, config.CriteriaText, config.CriteriaFilePath,
            string.Join("|", config.WorkingDirectories)))))[..16];
}

public static partial class PlanParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Parses the planner's JSON reply. Throws when no usable plan is found.</summary>
    public static LoopPlan Parse(string reply, string fallbackGoal, string fallbackCriteria)
    {
        foreach (var candidate in Candidates(reply))
        {
            try
            {
                var raw = JsonSerializer.Deserialize<RawPlan>(candidate, Options);
                if (raw is null) continue;

                var plan = new LoopPlan
                {
                    Summary = raw.Summary?.Trim() ?? "",
                    Issues = (raw.Issues ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Message)).Select(i => new PlanIssue
                    {
                        Severity = ParseSeverity(i.Severity),
                        Message = i.Message!.Trim(),
                        Suggestion = i.Suggestion?.Trim() ?? ""
                    }).ToList(),
                    Tasks = (raw.Tasks ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Title) || !string.IsNullOrWhiteSpace(t.Criteria)).Select(t => new PlanTask
                    {
                        Title = string.IsNullOrWhiteSpace(t.Title) ? "Task" : t.Title.Trim(),
                        Goal = t.Goal?.Trim() ?? "",
                        Criteria = t.Criteria?.Trim() ?? ""
                    }).ToList(),
                    SuggestedChecks = (raw.SuggestedChecks ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Command)).Select(c => new SuggestedCheck
                    {
                        Command = c.Command!.Trim(),
                        Verifies = c.Verifies?.Trim() ?? ""
                    }).ToList()
                };

                // A plan always has at least one task: the whole goal.
                if (plan.Tasks.Count == 0)
                    plan.Tasks.Add(new PlanTask { Title = "Whole goal", Goal = fallbackGoal, Criteria = fallbackCriteria });
                return plan;
            }
            catch (JsonException)
            {
                // try the next candidate
            }
        }
        throw new InvalidOperationException("The planner's reply did not contain a readable plan.");
    }

    private static PlanIssueSeverity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "blocker" or "critical" or "error" => PlanIssueSeverity.Blocker,
        "info" or "note" => PlanIssueSeverity.Info,
        _ => PlanIssueSeverity.Warning
    };

    private static IEnumerable<string> Candidates(string reply)
    {
        foreach (Match m in Fenced().Matches(reply)) yield return m.Groups[1].Value;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start >= 0 && end > start) yield return reply[start..(end + 1)];
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex Fenced();

    private sealed class RawPlan
    {
        public string? Summary { get; set; }
        public List<RawIssue>? Issues { get; set; }
        public List<RawTask>? Tasks { get; set; }
        public List<RawCheck>? SuggestedChecks { get; set; }
    }

    private sealed class RawIssue
    {
        public string? Severity { get; set; }
        public string? Message { get; set; }
        public string? Suggestion { get; set; }
    }

    private sealed class RawTask
    {
        public string? Title { get; set; }
        public string? Goal { get; set; }
        public string? Criteria { get; set; }
    }

    private sealed class RawCheck
    {
        public string? Command { get; set; }
        public string? Verifies { get; set; }
    }
}
