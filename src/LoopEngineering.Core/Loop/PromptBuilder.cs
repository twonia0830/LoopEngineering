using System.Text;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

public static class PromptBuilder
{
    /// <summary>Keep the progress history in the prompt bounded; the agent can read the full file itself.</summary>
    public const int MaxProgressChars = 24_000;

    public static string BuildWorkerPrompt(LoopConfig config, string goal, string criteria, int round, string progressPath, string progressContent)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are working inside an automated improvement loop. This is round {round} of at most {config.MaxLoops} for the goal \"{config.GoalName}\".");
        sb.AppendLine("Each round starts with a fresh context: everything you know about earlier rounds comes from the progress history below and from the files in the working folders.");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(goal.Trim());
        sb.AppendLine();
        sb.AppendLine("## Working folders");
        AppendFolders(sb, config);
        sb.AppendLine();
        sb.AppendLine("## Result criteria (the round is judged against these)");
        sb.AppendLine(criteria.Trim());
        sb.AppendLine();
        sb.AppendLine("## Progress history");
        sb.AppendLine($"Full file: `{progressPath}`");
        sb.AppendLine("```markdown");
        sb.AppendLine(Tail(progressContent, MaxProgressChars));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Instructions for this round");
        sb.AppendLine("- Inspect the current state, decide the most valuable remaining work toward the criteria, and do it now.");
        sb.AppendLine("- Address the evaluator's \"next steps\" from the previous round first, if any.");
        sb.AppendLine("- Use available skills and sub-agents when they fit the task.");
        sb.AppendLine("- Do not edit the progress file; the loop controller maintains it.");
        sb.AppendLine("- Finish your reply with a section `## Round summary` listing what you changed, how you verified it, and what is still missing.");
        if (!string.IsNullOrWhiteSpace(config.ExtraInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional instructions");
            sb.AppendLine(config.ExtraInstructions.Trim());
        }
        return sb.ToString();
    }

    public static string BuildEvaluatorPrompt(LoopConfig config, string goal, string criteria, int round, string workerOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the strict evaluator of round {round} of an automated loop working toward the goal \"{config.GoalName}\".");
        sb.AppendLine("Inspect the actual state of the working folders listed below (read files, run read-only checks or tests as needed). Do NOT modify any files.");
        sb.AppendLine("Judge only against the result criteria. The worker's report is a claim, not evidence: verify it.");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(goal.Trim());
        sb.AppendLine();
        sb.AppendLine("## Working folders");
        AppendFolders(sb, config);
        sb.AppendLine();
        sb.AppendLine("## Result criteria");
        sb.AppendLine(criteria.Trim());
        sb.AppendLine();
        sb.AppendLine("## Worker report for this round");
        sb.AppendLine(Tail(workerOutput, 12_000));
        sb.AppendLine();
        sb.AppendLine("## Response format");
        sb.AppendLine("Reply with ONLY a JSON object, no other text:");
        sb.AppendLine("{\"criteriaMet\": true|false, \"score\": 0-100, \"reasoning\": \"why, citing evidence\", \"nextSteps\": \"the most important work still missing (empty if met)\"}");
        sb.AppendLine("Set criteriaMet to true only if every criterion is fully satisfied.");
        return sb.ToString();
    }

    private static void AppendFolders(StringBuilder sb, LoopConfig config)
    {
        for (var i = 0; i < config.WorkingDirectories.Count; i++)
        {
            var dir = config.WorkingDirectories[i];
            var tags = new List<string>();
            if (i == 0) tags.Add("primary, current directory");
            if (Directory.Exists(Path.Combine(dir, ".git"))) tags.Add("git repo");
            sb.AppendLine($"- `{dir}`{(tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "")}");
        }
        if (config.WorkingDirectories.Count > 1)
            sb.AppendLine("The goal may require coordinated changes across these folders; keep them consistent with each other.");
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…(truncated, read the file for earlier rounds)…\n" + text[^maxChars..];
}
