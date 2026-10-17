using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Issues;

public interface IGitHubIssueFetcher
{
    Task<GitHubIssue> FetchAsync(GitHubIssueRef issue, string tokenVariable, CancellationToken cancellationToken);
}

/// <summary>
/// Reads issues (title, body, labels, comments) through the GitHub REST API, authenticated with the
/// same token variable as Copilot. A fine-grained token needs read access to the repo's issues;
/// public repos work with "Public repositories" access.
/// </summary>
public sealed class GitHubIssueFetcher(HttpClient http) : IGitHubIssueFetcher
{
    /// <summary>Most recent comments to include; long discussions are trimmed from the start.</summary>
    public const int MaxComments = 30;

    public async Task<GitHubIssue> FetchAsync(GitHubIssueRef issue, string tokenVariable, CancellationToken cancellationToken)
    {
        var token = TokenResolver.Resolve(tokenVariable);
        var baseUrl = $"{issue.ApiBase}/repos/{issue.Owner}/{issue.Repo}/issues/{issue.Number}";

        using var issueDoc = await GetJsonAsync(baseUrl, token, issue, cancellationToken);
        var root = issueDoc.RootElement;
        if (root.TryGetProperty("pull_request", out _))
            throw new InvalidOperationException($"{issue} is a pull request, not an issue.");

        var comments = new List<GitHubIssueComment>();
        var commentCount = root.TryGetProperty("comments", out var c) && c.TryGetInt32(out var n) ? n : 0;
        if (commentCount > 0)
        {
            // Ask for the last page so the newest comments are kept.
            var lastPage = Math.Max(1, (int)Math.Ceiling(commentCount / (double)MaxComments));
            using var commentsDoc = await GetJsonAsync($"{baseUrl}/comments?per_page={MaxComments}&page={lastPage}", token, issue, cancellationToken);
            foreach (var item in commentsDoc.RootElement.EnumerateArray())
            {
                comments.Add(new GitHubIssueComment(
                    item.GetProperty("user").GetProperty("login").GetString() ?? "unknown",
                    item.GetProperty("created_at").GetDateTimeOffset(),
                    item.GetProperty("body").GetString() ?? ""));
            }
        }

        return new GitHubIssue(
            issue,
            root.GetProperty("title").GetString() ?? "",
            root.GetProperty("state").GetString() ?? "",
            root.TryGetProperty("labels", out var labels)
                ? labels.EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").ToList()
                : [],
            root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "",
            comments);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string? token, GitHubIssueRef issue, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LoopEngineering", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "not found, or the token cannot see this repo (private repos need the token's repository access)",
                HttpStatusCode.Unauthorized => "the token was rejected",
                HttpStatusCode.Forbidden => "access denied or rate limited",
                _ => response.ReasonPhrase ?? "request failed"
            };
            throw new InvalidOperationException($"Could not read {issue}: {(int)response.StatusCode} {hint}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
