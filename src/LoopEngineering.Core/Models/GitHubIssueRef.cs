using System.Text.RegularExpressions;

namespace LoopEngineering.Core.Models;

/// <summary>
/// A GitHub issue reference parsed from a link (https://github.com/owner/repo/issues/123) or the
/// short form owner/repo#123. GitHub Enterprise hosts are supported by link.
/// </summary>
public sealed partial record GitHubIssueRef(string Host, string Owner, string Repo, int Number)
{
    public string HtmlUrl => $"https://{Host}/{Owner}/{Repo}/issues/{Number}";

    public string ApiBase => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        ? "https://api.github.com"
        : $"https://{Host}/api/v3";

    public override string ToString() => $"{Owner}/{Repo}#{Number}";

    public static bool TryParse(string text, out GitHubIssueRef issue)
    {
        text = text.Trim();
        var m = LinkPattern().Match(text);
        if (m.Success)
        {
            issue = new GitHubIssueRef(m.Groups["host"].Value, m.Groups["owner"].Value, m.Groups["repo"].Value, int.Parse(m.Groups["num"].Value));
            return true;
        }

        m = ShortPattern().Match(text);
        if (m.Success)
        {
            issue = new GitHubIssueRef("github.com", m.Groups["owner"].Value, m.Groups["repo"].Value, int.Parse(m.Groups["num"].Value));
            return true;
        }

        issue = null!;
        return false;
    }

    public static List<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [GeneratedRegex(@"^https?://(?<host>[^/\s]+)/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/issues/(?<num>\d+)(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^(?<owner>[\w.-]+)/(?<repo>[\w.-]+)#(?<num>\d+)$")]
    private static partial Regex ShortPattern();
}

/// <summary>Issue content fetched from GitHub, ready to put into a prompt.</summary>
public sealed record GitHubIssue(
    GitHubIssueRef Ref,
    string Title,
    string State,
    IReadOnlyList<string> Labels,
    string Body,
    IReadOnlyList<GitHubIssueComment> Comments);

public sealed record GitHubIssueComment(string Author, DateTimeOffset CreatedAt, string Body);
