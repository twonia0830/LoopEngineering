using System.Text;
using System.Text.RegularExpressions;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

/// <summary>What the agents get to know about one working folder before they start.</summary>
public sealed record RepoGuideContent(string Folder, string? AgentsMd, string? CopilotInstructions)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(AgentsMd) && string.IsNullOrWhiteSpace(CopilotInstructions);
}

/// <summary>
/// Maintains AGENTS.md in each working folder: a short, durable guide to the repo (purpose, layout,
/// build/test commands, conventions, gotchas) so every fresh session starts with a map instead of
/// re-exploring. The agent only replies with text; the app writes the file, so the guide steps never
/// touch anything else in the repo.
/// </summary>
public static partial class RepoGuide
{
    public const string FileName = "AGENTS.md";

    /// <summary>Upper bound for a guide the app will write; keeps prompts small and the file readable.</summary>
    public const int MaxGuideChars = 12_000;

    /// <summary>Per-folder cap when a guide is embedded in a prompt.</summary>
    public const int MaxPromptCharsPerFolder = 10_000;

    public const string NoChangesMarker = "NO_CHANGES";

    public static string PathFor(string folder) => Path.Combine(folder, FileName);

    public static RepoGuideContent Read(string folder)
    {
        static string? TryRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException) { return null; }
        }

        return new RepoGuideContent(
            folder,
            TryRead(PathFor(folder)),
            TryRead(Path.Combine(folder, ".github", "copilot-instructions.md")));
    }

    /// <param name="startingPoint">A user-written file (e.g. flow.md) naming entry points and key flows; path and content.</param>
    /// <param name="previousGuide">An existing AGENTS.md being regenerated; kept as reference only.</param>
    public static string BuildCreatePrompt(string folder, string? copilotInstructions,
        (string Path, string Content)? startingPoint = null, string? previousGuide = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Explore the repository in `{folder}` and write its {FileName}: a concise guide for AI coding agents (and people) who will work in it.");
        sb.AppendLine("Do NOT modify, create or delete any files. Only read files and run read-only commands (listing, viewing, git log/status). Do not build or run tests.");
        sb.AppendLine();
        if (startingPoint is { } start)
        {
            sb.AppendLine($"## Start here (written by the team, `{start.Path}`)");
            sb.AppendLine("Read this first. Begin your exploration at the entry points it names and trace the main flows it describes through the code,");
            sb.AppendLine("following calls across files, projects and services. It may describe several repos; focus on the parts that live in this folder.");
            sb.AppendLine("```markdown");
            sb.AppendLine(Truncate(start.Content.Trim(), 8_000));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("Cover, as far as you can verify from the repo itself:");
        sb.AppendLine("- What the project is for, in two or three sentences.");
        sb.AppendLine("- Layout: the important folders/projects and what lives where.");
        sb.AppendLine("- How to build, run and test it (exact commands, from build files, scripts or CI config).");
        sb.AppendLine("- Conventions: language/framework versions, code style, naming, patterns to follow.");
        sb.AppendLine("- Gotchas: anything non-obvious that would trip up someone new.");
        sb.AppendLine("Only state what the repo supports; mark guesses as such. Prefer short bullet points. Stay well under 150 lines.");
        if (!string.IsNullOrWhiteSpace(copilotInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("The repo already has .github/copilot-instructions.md; stay consistent with it and do not repeat it verbatim:");
            sb.AppendLine("```markdown");
            sb.AppendLine(Truncate(copilotInstructions, 6_000));
            sb.AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(previousGuide))
        {
            sb.AppendLine();
            sb.AppendLine($"The folder already has a {FileName}, which you are replacing. It may be outdated or incomplete: verify it against the code,");
            sb.AppendLine("keep what is still correct (especially hard-won gotchas), and drop or fix what is wrong:");
            sb.AppendLine("```markdown");
            sb.AppendLine(Truncate(previousGuide.Trim(), 8_000));
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine($"Reply with ONLY the Markdown content of {FileName}, starting with a `# ` heading. No preamble, no code fence around the whole reply.");
        return sb.ToString();
    }

    public static string BuildUpdatePrompt(string folder, string currentGuide, string goal, string runSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"A task in `{folder}` was just completed successfully. Decide whether the repo's {FileName} should be updated.");
        sb.AppendLine("Do NOT modify any files yourself. You may read files and run read-only commands such as `git status` and `git diff --stat` to see what changed.");
        sb.AppendLine();
        sb.AppendLine("Add only DURABLE knowledge that will help future work in this repo, such as new or changed build/test commands,");
        sb.AppendLine("new projects or folders, conventions that were established, or gotchas that were discovered. Fix statements that are now wrong.");
        sb.AppendLine("Do NOT add a history of this task, round-by-round notes, or anything specific to this one change.");
        sb.AppendLine("Keep the existing structure and wording where it is still correct, and keep the guide concise.");
        sb.AppendLine();
        sb.AppendLine("## Task goal");
        sb.AppendLine(Truncate(goal.Trim(), 4_000));
        sb.AppendLine();
        sb.AppendLine("## What happened (summaries from the loop)");
        sb.AppendLine(Truncate(runSummary.Trim(), 8_000));
        sb.AppendLine();
        sb.AppendLine($"## Current {FileName}");
        sb.AppendLine("```markdown");
        sb.AppendLine(currentGuide.Trim());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"If nothing durable should change, reply with exactly {NoChangesMarker}.");
        sb.AppendLine($"Otherwise reply with ONLY the complete updated {FileName} content. No preamble, no code fence around the whole reply.");
        return sb.ToString();
    }

    /// <summary>Normalizes an agent reply into guide content; null means "no change" or nothing usable.</summary>
    public static string? ParseReply(string reply)
    {
        var text = reply.Trim();
        if (text.Length == 0 || text.Equals(NoChangesMarker, StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith(NoChangesMarker, StringComparison.OrdinalIgnoreCase) && text.Length < 200)
            return null;

        // Unwrap a reply fenced as a whole.
        var fenced = WholeFence().Match(text);
        if (fenced.Success) text = fenced.Groups[1].Value.Trim();

        // Drop any chatter before the first top-level heading.
        var heading = LineStartHeading().Match(text);
        if (!heading.Success) return null;
        return text[heading.Index..].TrimEnd() + Environment.NewLine;
    }

    public static void Write(string folder, string content) =>
        File.WriteAllText(PathFor(folder), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>The worker's "## Round summary" section, or the tail of its reply.</summary>
    public static string ExtractRoundSummary(string workerOutput)
    {
        var index = workerOutput.LastIndexOf("## Round summary", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? workerOutput[index..].Trim() : Truncate(workerOutput.Trim(), 1_500, fromEnd: true);
    }

    public static string Truncate(string text, int max, bool fromEnd = false) =>
        text.Length <= max ? text : fromEnd ? "…" + text[^max..] : text[..max] + "\n…(truncated)";

    [GeneratedRegex(@"^```(?:markdown|md)?\s*\n(.*)\n```$", RegexOptions.Singleline)]
    private static partial Regex WholeFence();

    [GeneratedRegex(@"(?m)^# ")]
    private static partial Regex LineStartHeading();
}
