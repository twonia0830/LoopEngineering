using System.Text;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

/// <summary>Maintains {goal}_progress.md, one section per loop round.</summary>
public sealed class ProgressFile
{
    public string Path { get; }

    public ProgressFile(string directory, string goalName)
    {
        Path = System.IO.Path.Combine(directory, $"{SanitizeFileName(goalName)}_progress.md");
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return string.IsNullOrEmpty(cleaned) ? "loop" : cleaned;
    }

    public string Read() => File.Exists(Path) ? File.ReadAllText(Path) : "";

    /// <summary>Creates the file, or appends a new run header if it already exists (history is never erased).</summary>
    public void WriteRunHeader(LoopConfig config, string goal, string goalSource, string criteria, DateTimeOffset startedAt)
    {
        var sb = new StringBuilder();
        if (!File.Exists(Path))
        {
            sb.AppendLine($"# {config.GoalName} — Loop Progress");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine($"## Run started {startedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"- **Max loops:** {config.MaxLoops}");
        sb.AppendLine($"- **Working folders:** {string.Join(", ", config.WorkingDirectories.Select(d => $"`{d}`"))}");
        sb.AppendLine($"- **Goal source:** {goalSource}");
        sb.AppendLine($"- **Model:** {(string.IsNullOrWhiteSpace(config.Model) ? "(default)" : config.Model)}");
        sb.AppendLine($"- **Criteria source:** {(config.CriteriaSource == CriteriaSource.File ? $"file `{config.CriteriaFilePath}`" : "text")}");
        if (!string.IsNullOrWhiteSpace(config.ActiveAgent)) sb.AppendLine($"- **Agent:** {config.ActiveAgent}");
        if (config.SkillDirectories.Count > 0) sb.AppendLine($"- **Skill directories:** {string.Join(", ", config.SkillDirectories.Select(d => $"`{d}`"))}");
        sb.AppendLine();
        sb.AppendLine("### Goal");
        sb.AppendLine(goal.Trim());
        sb.AppendLine();
        sb.AppendLine("### Result criteria");
        sb.AppendLine(criteria.Trim());
        sb.AppendLine();
        Append(sb.ToString());
    }

    public void AppendRound(RoundResult round, int maxLoops)
    {
        var sb = new StringBuilder();
        var eval = round.Evaluation;
        var verdict = round.Status switch
        {
            RoundStatus.Failed => "FAILED",
            RoundStatus.Cancelled => "CANCELLED",
            _ when eval?.CriteriaMet == true => "CRITERIA MET",
            _ => "NOT MET"
        };

        sb.AppendLine($"### Round {round.Number}/{maxLoops} — {verdict}{(eval?.Score is int s ? $" (score {s}/100)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"- **Started:** {round.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Duration:** {FormatDuration(round.Duration)}");
        sb.AppendLine($"- **Tool calls:** {round.ToolCalls}");
        if (!round.Usage.IsEmpty) sb.AppendLine($"- **Tokens:** {round.Usage}");
        if (round.Usage.Models.Count > 0) sb.AppendLine($"- **Models used:** {round.Usage.ModelsLabel}");
        if (round.Subagents.Count > 0)
            sb.AppendLine($"- **Sub-agents:** {string.Join(", ", round.Subagents.Select(a => $"{a.Name} ({(a.Succeeded == false ? "failed" : "ok")})"))}");
        if (round.Error is not null)
            sb.AppendLine($"- **Error:** {round.Error}");
        sb.AppendLine();

        if (eval is not null)
        {
            sb.AppendLine("**Evaluator reasoning:**");
            sb.AppendLine();
            sb.AppendLine(eval.Reasoning.Trim());
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(eval.NextSteps))
            {
                sb.AppendLine("**Next steps:**");
                sb.AppendLine();
                sb.AppendLine(eval.NextSteps.Trim());
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(round.WorkerOutput))
        {
            sb.AppendLine("<details><summary>Worker output</summary>");
            sb.AppendLine();
            sb.AppendLine(round.WorkerOutput.Trim());
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
        Append(sb.ToString());
    }

    public void AppendFinal(RunStatus status, int roundsRun, DateTimeOffset endedAt, TokenUsage? total = null)
    {
        var text = status switch
        {
            RunStatus.CriteriaMet => $"Criteria met after {roundsRun} round(s).",
            RunStatus.MaxLoopsReached => $"Max loops reached ({roundsRun}) without meeting the criteria.",
            RunStatus.Stopped => $"Stopped by user after {roundsRun} round(s).",
            RunStatus.Failed => $"Run failed after {roundsRun} round(s).",
            _ => status.ToString()
        };
        var usage = total is { IsEmpty: false } ? $"\n\nTotal tokens: {total}" : "";
        Append($"### Result — {text}\n\nEnded {endedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}{usage}\n");
    }

    private void Append(string text) => File.AppendAllText(Path, text, Encoding.UTF8);

    public static string FormatDuration(TimeSpan? d) => d is null
        ? "-"
        : d.Value.TotalHours >= 1 ? d.Value.ToString(@"h\:mm\:ss") : d.Value.ToString(@"m\:ss");
}
