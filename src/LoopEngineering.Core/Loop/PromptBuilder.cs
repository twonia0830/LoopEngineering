using System.Text;
using LoopEngineering.Core.Planning;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

public static class PromptBuilder
{
    /// <summary>Keep the progress history in the prompt bounded; the agent can read the full file itself.</summary>
    public const int MaxProgressChars = 24_000;

    public static string BuildWorkerPrompt(LoopConfig config, string goal, string criteria, int round, string progressPath, string progressContent,
        IReadOnlyList<RepoGuideContent>? guides = null, LoopPlan? plan = null)
    {
        var task = plan?.CurrentTask;
        var sb = new StringBuilder();
        sb.AppendLine($"You are working inside an automated improvement loop. This is round {round} of at most {config.MaxLoops} for the goal \"{config.GoalName}\".");
        sb.AppendLine("Each round starts with a fresh context: everything you know about earlier rounds comes from the progress history below and from the files in the working folders.");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(goal.Trim());
        sb.AppendLine();
        sb.AppendLine("## Working folders");
        AppendFolders(sb, config);
        AppendGuides(sb, guides);
        sb.AppendLine();
        if (plan is not null && task is not null)
        {
            AppendPlan(sb, plan, task);
            sb.AppendLine("## Overall result criteria (checked once every task has passed)");
            sb.AppendLine(criteria.Trim());
        }
        else
        {
            sb.AppendLine("## Result criteria (the round is judged against these)");
            sb.AppendLine(criteria.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("## Progress history");
        sb.AppendLine($"Full file: `{progressPath}`");
        sb.AppendLine("```markdown");
        sb.AppendLine(Tail(progressContent, MaxProgressChars));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Instructions for this round");
        sb.AppendLine(task is not null && plan!.Tasks.Count > 1
            ? "- Work only on the current task above; later tasks get their own rounds."
            : "- Inspect the current state, decide the most valuable remaining work toward the criteria, and do it now.");
        sb.AppendLine("- Address the evaluator's \"next steps\" from the previous round first, if any.");
        sb.AppendLine("- Use available skills and sub-agents when they fit the task.");
        sb.AppendLine("- Do not edit the progress file; the loop controller maintains it.");
        if (guides is { Count: > 0 })
            sb.AppendLine($"- Do not edit {RepoGuide.FileName}; it is updated after the run from what you report.");
        sb.AppendLine("- Finish your reply with a section `## Round summary` listing what you changed, how you verified it, and what is still missing.");
        if (!string.IsNullOrWhiteSpace(config.ExtraInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("## Additional instructions");
            sb.AppendLine(config.ExtraInstructions.Trim());
        }
        return sb.ToString();
    }

    public static string BuildEvaluatorPrompt(LoopConfig config, string goal, string criteria, int round, string workerOutput,
        IReadOnlyList<RepoGuideContent>? guides = null, PlanTask? task = null)
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
        AppendGuides(sb, guides);
        sb.AppendLine();
        if (task is not null)
        {
            sb.AppendLine($"## Task under review: {task.Title}");
            if (!string.IsNullOrWhiteSpace(task.Goal)) sb.AppendLine(task.Goal.Trim());
            sb.AppendLine();
            sb.AppendLine("## Result criteria for this task (judge only these)");
            sb.AppendLine(task.Criteria.Trim());
        }
        else
        {
            sb.AppendLine("## Result criteria");
            sb.AppendLine(criteria.Trim());
        }
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

    public static string BuildPlannerPrompt(LoopConfig config, string goal, string criteria, IReadOnlyList<RepoGuideContent>? guides = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the planner for an automated loop that will work toward the goal \"{config.GoalName}\" in up to {config.MaxLoops} rounds.");
        sb.AppendLine("Each round, a worker agent with a fresh context makes changes and an independent evaluator judges them against acceptance criteria.");
        sb.AppendLine("Before the loop starts, review the goal and the criteria so the loop does not waste rounds. Inspect the working folders as needed (read files, list folders).");
        sb.AppendLine("Do NOT modify, create or delete any files.");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(goal.Trim());
        sb.AppendLine();
        sb.AppendLine("## Working folders");
        AppendFolders(sb, config);
        AppendGuides(sb, guides);
        sb.AppendLine();
        sb.AppendLine("## Acceptance criteria as written");
        sb.AppendLine(criteria.Trim());
        sb.AppendLine();
        sb.AppendLine("## What to do");
        sb.AppendLine("1. Find problems: criteria that are vague or subjective (an evaluator cannot verify them), goals that conflict with the code you see,");
        sb.AppendLine("   missing information the worker would need, and anything that cannot be done in the working folders.");
        sb.AppendLine("   Use \"blocker\" only when the loop would likely fail or waste rounds until it is fixed.");
        sb.AppendLine("2. Decide the task list. If the goal fits comfortably in one round, return exactly ONE task that restates the whole goal with sharpened criteria.");
        sb.AppendLine($"   If it is too big for one round, split it into ordered tasks (at most {Math.Max(1, config.MaxLoops)}), each small enough for one round,");
        sb.AppendLine("   each with its own concrete, checkable criteria. Together the tasks must satisfy all the acceptance criteria.");
        sb.AppendLine("3. Suggest commands that would check criteria deterministically (build, tests, a script), if any apply. They are shown to the user, not run.");
        sb.AppendLine();
        sb.AppendLine("## Response format");
        sb.AppendLine("Reply with ONLY a JSON object, no other text:");
        sb.AppendLine("{\"summary\": \"one or two sentences on the plan\",");
        sb.AppendLine(" \"issues\": [{\"severity\": \"blocker|warning|info\", \"message\": \"what is wrong\", \"suggestion\": \"how to fix it\"}],");
        sb.AppendLine(" \"tasks\": [{\"title\": \"short title\", \"goal\": \"what to do in this task\", \"criteria\": \"checkable criteria, one per line\"}],");
        sb.AppendLine(" \"suggestedChecks\": [{\"command\": \"exact command\", \"verifies\": \"which criterion it checks\"}]}");
        return sb.ToString();
    }

    private static void AppendPlan(StringBuilder sb, LoopPlan plan, PlanTask current)
    {
        if (plan.Tasks.Count > 1)
        {
            sb.AppendLine("## Plan");
            for (var i = 0; i < plan.Tasks.Count; i++)
            {
                var t = plan.Tasks[i];
                var mark = t.Status == PlanTaskStatus.Passed ? "done" : ReferenceEquals(t, current) ? "CURRENT" : "later";
                sb.AppendLine($"{i + 1}. [{mark}] {t.Title}");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"## Current task: {current.Title}");
        if (!string.IsNullOrWhiteSpace(current.Goal)) sb.AppendLine(current.Goal.Trim());
        sb.AppendLine();
        sb.AppendLine("## Task criteria (this round is judged against these)");
        sb.AppendLine(current.Criteria.Trim());
        sb.AppendLine();
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

    /// <summary>Embeds each folder's AGENTS.md (and copilot-instructions) so a fresh session starts with a map of the repo.</summary>
    private static void AppendGuides(StringBuilder sb, IReadOnlyList<RepoGuideContent>? guides)
    {
        foreach (var guide in guides ?? [])
        {
            if (guide.IsEmpty) continue;
            sb.AppendLine();
            sb.AppendLine($"### Repo guide for `{guide.Folder}`");
            if (!string.IsNullOrWhiteSpace(guide.AgentsMd))
            {
                sb.AppendLine($"From {RepoGuide.FileName}:");
                sb.AppendLine(RepoGuide.Truncate(guide.AgentsMd.Trim(), RepoGuide.MaxPromptCharsPerFolder));
            }
            if (!string.IsNullOrWhiteSpace(guide.CopilotInstructions))
            {
                sb.AppendLine();
                sb.AppendLine("From .github/copilot-instructions.md:");
                sb.AppendLine(RepoGuide.Truncate(guide.CopilotInstructions.Trim(), RepoGuide.MaxPromptCharsPerFolder / 2));
            }
        }
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…(truncated, read the file for earlier rounds)…\n" + text[^maxChars..];
}
