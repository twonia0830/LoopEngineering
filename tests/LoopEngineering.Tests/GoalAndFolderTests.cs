using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Issues;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class GoalAndFolderTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("loopeng-a-").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("loopeng-b-").FullName;

    public void Dispose()
    {
        Directory.Delete(_a, recursive: true);
        Directory.Delete(_b, recursive: true);
    }

    [Theory]
    [InlineData("https://github.com/octo/repo/issues/42", "github.com", "octo", "repo", 42)]
    [InlineData("https://github.com/octo/repo/issues/42#issuecomment-1", "github.com", "octo", "repo", 42)]
    [InlineData("  octo/my.repo#7 ", "github.com", "octo", "my.repo", 7)]
    [InlineData("https://ghe.contoso.com/team/app/issues/3", "ghe.contoso.com", "team", "app", 3)]
    public void Parses_issue_links(string text, string host, string owner, string repo, int number)
    {
        Assert.True(GitHubIssueRef.TryParse(text, out var issue));
        Assert.Equal(new GitHubIssueRef(host, owner, repo, number), issue);
    }

    [Theory]
    [InlineData("https://github.com/octo/repo/pull/42")]
    [InlineData("just some text")]
    public void Rejects_non_issue_links(string text) => Assert.False(GitHubIssueRef.TryParse(text, out _));

    [Fact]
    public void Enterprise_issue_uses_enterprise_api() =>
        Assert.Equal("https://ghe.contoso.com/api/v3", new GitHubIssueRef("ghe.contoso.com", "t", "a", 1).ApiBase);

    [Fact]
    public async Task Fetcher_reads_issue_and_comments_with_token()
    {
        Environment.SetEnvironmentVariable("LOOP_TEST_TOKEN", "tok123");
        try
        {
            var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/comments")
                ? """[{"user":{"login":"alice"},"created_at":"2026-10-01T00:00:00Z","body":"Repro attached"}]"""
                : """{"title":"Crash on save","state":"open","labels":[{"name":"bug"}],"body":"It crashes.","comments":1}""");
            var fetcher = new GitHubIssueFetcher(new HttpClient(handler));

            var issue = await fetcher.FetchAsync(new GitHubIssueRef("github.com", "octo", "repo", 5), "LOOP_TEST_TOKEN", default);

            Assert.Equal("Crash on save", issue.Title);
            Assert.Equal(["bug"], issue.Labels);
            Assert.Equal("alice", issue.Comments.Single().Author);
            Assert.Equal("https://api.github.com/repos/octo/repo/issues/5", handler.Requests[0].RequestUri!.ToString());
            Assert.Equal("Bearer tok123", handler.Requests[0].Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOOP_TEST_TOKEN", null);
        }
    }

    [Fact]
    public async Task Fetcher_explains_not_found()
    {
        var fetcher = new GitHubIssueFetcher(new HttpClient(new FakeHandler(_ => null)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.FetchAsync(new GitHubIssueRef("github.com", "octo", "private", 1), "NO_SUCH_VAR", default));
        Assert.Contains("404", ex.Message);
        Assert.Contains("repository access", ex.Message);
    }

    [Fact]
    public async Task Issues_goal_is_fetched_once_and_sent_with_all_folders()
    {
        var fetcher = new FakeFetcher();
        var agent = new RecordingAgent();
        var runner = new LoopRunner(new SingleFactory(agent), fetcher);

        runner.Start(new LoopConfig
        {
            GoalName = "multi",
            GoalSource = GoalSource.GitHubIssues,
            GoalIssueLinks = "https://github.com/octo/api/issues/1\nocto/web#2",
            CriteriaText = "fixed",
            MaxLoops = 2,
            WorkingDirectories = [_a, _b]
        });
        await runner.WaitForCompletionAsync();

        Assert.Equal(2, fetcher.Calls); // two issues, fetched once for the whole run
        var worker = agent.Prompts.First(p => p.Phase == RoundPhase.Worker).Prompt;
        Assert.Contains("octo/api#1: Title 1", worker);
        Assert.Contains("octo/web#2: Title 2", worker);
        Assert.Contains($"`{_a}` (primary", worker);
        Assert.Contains($"`{_b}`", worker);
        Assert.Contains("GitHub issues: https://github.com/octo/api/issues/1", File.ReadAllText(runner.GetSnapshot().ProgressFilePath!));
    }

    [Fact]
    public async Task Failed_issue_fetch_fails_run_before_any_turn()
    {
        var agent = new RecordingAgent();
        var runner = new LoopRunner(new SingleFactory(agent), new FakeFetcher(fail: true));

        runner.Start(new LoopConfig
        {
            GoalName = "g", GoalSource = GoalSource.GitHubIssues, GoalIssueLinks = "octo/api#1",
            CriteriaText = "c", WorkingDirectories = [_a]
        });
        await runner.WaitForCompletionAsync();

        Assert.Equal(RunStatus.Failed, runner.GetSnapshot().Status);
        Assert.Empty(agent.Prompts);
    }

    [Fact]
    public void Progress_folder_overrides_first_working_folder()
    {
        var config = new LoopConfig { WorkingDirectories = [_a, _b], ProgressDirectory = _b };
        Assert.Equal(_b, config.EffectiveProgressDirectory);
        config.ProgressDirectory = "";
        Assert.Equal(_a, config.EffectiveProgressDirectory);
    }

    [Fact]
    public void Validation_covers_goal_sources_and_folders()
    {
        var config = new LoopConfig
        {
            GoalName = "g", CriteriaText = "c",
            GoalSource = GoalSource.GitHubIssues, GoalIssueLinks = "not a link",
            WorkingDirectories = [_a, _a.ToUpperInvariant(), Path.Combine(_a, "missing")]
        };
        var errors = config.Validate();
        Assert.Contains(errors, e => e.Contains("Not a GitHub issue link"));
        Assert.Contains(errors, e => e.Contains("listed twice"));
        Assert.Contains(errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public void Old_saved_config_moves_working_directory_into_list()
    {
        var path = Path.Combine(_a, "cfg.json");
        File.WriteAllText(path, $$"""{"GoalName":"x","WorkingDirectory":{{JsonSerializer.Serialize(_b)}}}""");

        var config = new ConfigStore(path).Load();
        Assert.Equal([_b], config.WorkingDirectories);

        new ConfigStore(path).Save(config);
        Assert.DoesNotContain("\"WorkingDirectory\"", File.ReadAllText(path));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, string?> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = respond(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class FakeFetcher(bool fail = false) : IGitHubIssueFetcher
    {
        public int Calls;

        public Task<GitHubIssue> FetchAsync(GitHubIssueRef issue, string tokenVariable, CancellationToken cancellationToken)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("boom");
            return Task.FromResult(new GitHubIssue(issue, $"Title {issue.Number}", "open", [], "body", []));
        }
    }

    private sealed class SingleFactory(ILoopAgent agent) : ILoopAgentFactory
    {
        public ILoopAgent Create(LoopConfig config) => agent;
    }

    private sealed class RecordingAgent : ILoopAgent
    {
        public List<AgentTurnRequest> Prompts { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
        {
            Prompts.Add(request);
            return Task.FromResult(request.Phase == RoundPhase.Worker ? "work" : "{\"criteriaMet\": false}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
