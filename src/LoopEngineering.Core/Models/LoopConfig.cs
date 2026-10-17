using System.Text.Json.Serialization;

namespace LoopEngineering.Core.Models;

public enum GoalSource
{
    Text,
    File,
    GitHubIssues
}

public enum CriteriaSource
{
    Text,
    File
}

/// <summary>
/// Everything the user configures before starting a loop run.
/// </summary>
public sealed class LoopConfig
{
    /// <summary>Goal / project name. Also used for the progress file name: {GoalName}_progress.md.</summary>
    public string GoalName { get; set; } = "";

    public GoalSource GoalSource { get; set; } = GoalSource.Text;

    /// <summary>What the agent should achieve (used when <see cref="GoalSource"/> is Text).</summary>
    public string GoalDescription { get; set; } = "";

    /// <summary>Path to a goal file (used when <see cref="GoalSource"/> is File). Re-read every round.</summary>
    public string GoalFilePath { get; set; } = "";

    /// <summary>GitHub issue links, one per line (used when <see cref="GoalSource"/> is GitHubIssues). Fetched once per run.</summary>
    public string GoalIssueLinks { get; set; } = "";

    public CriteriaSource CriteriaSource { get; set; } = CriteriaSource.Text;

    /// <summary>Result criteria text (used when <see cref="CriteriaSource"/> is Text).</summary>
    public string CriteriaText { get; set; } = "";

    /// <summary>Path to a criteria file (used when <see cref="CriteriaSource"/> is File). Re-read every round.</summary>
    public string CriteriaFilePath { get; set; } = "";

    public int MaxLoops { get; set; } = 5;

    /// <summary>
    /// Folders the agent works in, usually cloned repos. The first is the session's working directory;
    /// the rest are granted as additional directories so one goal can span several repos.
    /// </summary>
    public List<string> WorkingDirectories { get; set; } = [];

    /// <summary>Where {GoalName}_progress.md is written. Empty = the first working folder.</summary>
    public string ProgressDirectory { get; set; } = "";

    [JsonIgnore]
    public string PrimaryWorkingDirectory => WorkingDirectories.FirstOrDefault() ?? "";

    [JsonIgnore]
    public string EffectiveProgressDirectory =>
        string.IsNullOrWhiteSpace(ProgressDirectory) ? PrimaryWorkingDirectory : ProgressDirectory;

    /// <summary>Reads configs saved before multiple working folders existed. Never written back.</summary>
    [JsonPropertyName("WorkingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyWorkingDirectory
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && WorkingDirectories.Count == 0) WorkingDirectories.Add(value);
        }
    }

    /// <summary>Worker model id, e.g. "gpt-5-mini". Empty = runtime default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Worker reasoning effort ("low" … "max") or empty for the model default.</summary>
    public string ReasoningEffort { get; set; } = "";

    /// <summary>
    /// Evaluator model id. Empty = same as the worker. A different model (ideally another provider)
    /// is less likely to share the worker's blind spots or favour its own style of output.
    /// </summary>
    public string EvaluatorModel { get; set; } = "";

    /// <summary>
    /// Evaluator reasoning effort. Empty = the worker's effort when the evaluator uses the worker's model,
    /// otherwise the evaluator model's default.
    /// </summary>
    public string EvaluatorReasoningEffort { get; set; } = "";

    public string ModelFor(RoundPhase phase) =>
        phase == RoundPhase.Evaluator && !string.IsNullOrWhiteSpace(EvaluatorModel) ? EvaluatorModel : Model;

    public string ReasoningEffortFor(RoundPhase phase)
    {
        if (phase != RoundPhase.Evaluator) return ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(EvaluatorReasoningEffort)) return EvaluatorReasoningEffort;
        return string.IsNullOrWhiteSpace(EvaluatorModel) ? ReasoningEffort : "";
    }

    /// <summary>
    /// Name of the environment variable that holds the GitHub token.
    /// The token is read from this variable only and handed to the SDK explicitly;
    /// logged-in-user lookup and the well-known token variables are disabled.
    /// </summary>
    public string TokenEnvironmentVariable { get; set; } = "COPILOT_LOOP_TOKEN";

    /// <summary>Folders containing skills (each skill is a sub folder with SKILL.md).</summary>
    public List<string> SkillDirectories { get; set; } = [];

    /// <summary>Folders containing agent definition files (*.agent.md / *.md).</summary>
    public List<string> AgentDirectories { get; set; } = [];

    /// <summary>Agents defined inline in the UI.</summary>
    public List<CustomAgentDefinition> CustomAgents { get; set; } = [];

    /// <summary>Name of an agent to run the worker turn as. Empty = default agent.</summary>
    public string ActiveAgent { get; set; } = "";

    /// <summary>Extra instructions appended to every worker prompt.</summary>
    public string ExtraInstructions { get; set; } = "";

    /// <summary>Max minutes a single worker / evaluator turn may take.</summary>
    public int TurnTimeoutMinutes { get; set; } = 30;

    /// <summary>Abort the run after this many rounds fail in a row (exceptions, not unmet criteria).</summary>
    public int MaxConsecutiveFailures { get; set; } = 2;

    /// <summary>
    /// Let the runtime discover config (instructions, skills, agents, MCP) from the working directory and ~/.copilot.
    /// Off by default so only what is configured here is used.
    /// </summary>
    public bool EnableConfigDiscovery { get; set; }

    public LoopConfig Clone()
    {
        var copy = (LoopConfig)MemberwiseClone();
        copy.WorkingDirectories = [.. WorkingDirectories];
        copy.SkillDirectories = [.. SkillDirectories];
        copy.AgentDirectories = [.. AgentDirectories];
        copy.CustomAgents = CustomAgents.Select(a => a.Clone()).ToList();
        return copy;
    }

    /// <summary>Returns a list of problems; empty when the config can be started.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(GoalName)) errors.Add("Goal name is required.");
        switch (GoalSource)
        {
            case GoalSource.Text when string.IsNullOrWhiteSpace(GoalDescription):
                errors.Add("Goal description is required.");
                break;
            case GoalSource.File when string.IsNullOrWhiteSpace(GoalFilePath):
                errors.Add("Goal file path is required.");
                break;
            case GoalSource.File when !File.Exists(GoalFilePath):
                errors.Add($"Goal file not found: {GoalFilePath}");
                break;
            case GoalSource.GitHubIssues:
                var lines = GitHubIssueRef.SplitLines(GoalIssueLinks);
                if (lines.Count == 0) errors.Add("Add at least one GitHub issue link.");
                foreach (var line in lines)
                    if (!GitHubIssueRef.TryParse(line, out _)) errors.Add($"Not a GitHub issue link: {line}");
                break;
        }
        if (MaxLoops < 1) errors.Add("Max loops must be at least 1.");
        if (TurnTimeoutMinutes < 1) errors.Add("Turn timeout must be at least 1 minute.");
        if (WorkingDirectories.Count == 0) errors.Add("Add at least one working folder.");
        foreach (var dir in WorkingDirectories)
            if (!Directory.Exists(dir)) errors.Add($"Working folder does not exist: {dir}");
        if (WorkingDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != WorkingDirectories.Count)
            errors.Add("The same working folder is listed twice.");
        if (!string.IsNullOrWhiteSpace(ProgressDirectory) && !Directory.Exists(ProgressDirectory))
            errors.Add($"Progress folder does not exist: {ProgressDirectory}");

        if (CriteriaSource == CriteriaSource.Text && string.IsNullOrWhiteSpace(CriteriaText))
            errors.Add("Result criteria text is required.");
        if (CriteriaSource == CriteriaSource.File)
        {
            if (string.IsNullOrWhiteSpace(CriteriaFilePath)) errors.Add("Criteria file path is required.");
            else if (!File.Exists(CriteriaFilePath)) errors.Add($"Criteria file not found: {CriteriaFilePath}");
        }

        if (string.IsNullOrWhiteSpace(TokenEnvironmentVariable))
            errors.Add("Token environment variable name is required.");

        foreach (var dir in SkillDirectories.Concat(AgentDirectories))
            if (!Directory.Exists(dir)) errors.Add($"Directory not found: {dir}");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in CustomAgents)
        {
            if (string.IsNullOrWhiteSpace(agent.Name)) errors.Add("Every custom agent needs a name.");
            else if (!names.Add(agent.Name)) errors.Add($"Duplicate custom agent name: {agent.Name}");
            if (string.IsNullOrWhiteSpace(agent.Prompt)) errors.Add($"Custom agent '{agent.Name}' needs a prompt.");
        }

        return errors;
    }
}

public sealed class CustomAgentDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";

    /// <summary>Skill names (resolved from the skill directories) preloaded into this agent.</summary>
    public List<string> Skills { get; set; } = [];

    public CustomAgentDefinition Clone() => new()
    {
        Name = Name,
        Description = Description,
        Prompt = Prompt,
        Skills = [.. Skills]
    };
}
