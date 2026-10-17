using System.Text;
using LoopEngineering.Core.Issues;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

/// <summary>
/// Turns the configured goal source into the goal text sent to the agent. Files are re-read every
/// round (so they can be edited while the loop runs); GitHub issues are fetched once per run.
/// </summary>
public sealed class GoalResolver(LoopConfig config, IGitHubIssueFetcher? issueFetcher)
{
    /// <summary>Keeps one huge issue thread from crowding out the rest of the prompt.</summary>
    public const int MaxIssueBodyChars = 8_000;
    public const int MaxCommentChars = 2_000;

    private string? _issuesText;

    /// <summary>Short description of where the goal comes from, for the progress file.</summary>
    public string SourceDescription => config.GoalSource switch
    {
        GoalSource.File => $"file `{config.GoalFilePath}`",
        GoalSource.GitHubIssues => "GitHub issues: " + string.Join(", ",
            GitHubIssueRef.SplitLines(config.GoalIssueLinks).Select(l => GitHubIssueRef.TryParse(l, out var r) ? r.HtmlUrl : l)),
        _ => "text"
    };

    public async Task<string> ResolveAsync(CancellationToken cancellationToken)
    {
        switch (config.GoalSource)
        {
            case GoalSource.File:
                return await File.ReadAllTextAsync(config.GoalFilePath, cancellationToken);
            case GoalSource.GitHubIssues:
                return _issuesText ??= Format(await FetchIssuesAsync(config, issueFetcher, cancellationToken));
            default:
                return config.GoalDescription;
        }
    }

    public static async Task<IReadOnlyList<GitHubIssue>> FetchIssuesAsync(
        LoopConfig config, IGitHubIssueFetcher? fetcher, CancellationToken cancellationToken)
    {
        if (fetcher is null) throw new InvalidOperationException("GitHub issue fetching is not configured.");

        var refs = GitHubIssueRef.SplitLines(config.GoalIssueLinks)
            .Select(line => GitHubIssueRef.TryParse(line, out var r) ? r : throw new InvalidOperationException($"Not a GitHub issue link: {line}"))
            .Distinct()
            .ToList();

        var issues = new List<GitHubIssue>();
        foreach (var r in refs)
            issues.Add(await fetcher.FetchAsync(r, config.TokenEnvironmentVariable, cancellationToken));
        return issues;
    }

    public static string Format(IReadOnlyList<GitHubIssue> issues)
    {
        var sb = new StringBuilder();
        sb.AppendLine(issues.Count == 1
            ? "Resolve the following GitHub issue."
            : $"Resolve the following {issues.Count} GitHub issues. They may touch different repos among the working folders.");

        foreach (var issue in issues)
        {
            sb.AppendLine();
            sb.AppendLine($"### {issue.Ref}: {issue.Title}");
            sb.AppendLine($"- Link: {issue.Ref.HtmlUrl}");
            sb.AppendLine($"- State: {issue.State}");
            if (issue.Labels.Count > 0) sb.AppendLine($"- Labels: {string.Join(", ", issue.Labels)}");
            sb.AppendLine();
            sb.AppendLine(Truncate(string.IsNullOrWhiteSpace(issue.Body) ? "_(no description)_" : issue.Body.Trim(), MaxIssueBodyChars));

            if (issue.Comments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"#### Comments ({issue.Comments.Count} most recent)");
                foreach (var comment in issue.Comments)
                {
                    sb.AppendLine();
                    sb.AppendLine($"**{comment.Author}** ({comment.CreatedAt:yyyy-MM-dd}):");
                    sb.AppendLine(Truncate(comment.Body.Trim(), MaxCommentChars));
                }
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "\n…(truncated)";
}
