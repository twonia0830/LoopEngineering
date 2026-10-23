using LoopEngineering.Core.Agents;
using LoopEngineering.Core.ClaudeCode;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class ClaudeCodeTests
{
    private static LoopConfig Config() => new()
    {
        WorkingDirectories = [@"C:\repo\api", @"C:\repo\web"],
        WorkerBackend = AgentBackend.Copilot,
        Model = "gpt-5-mini",
        EvaluatorBackend = AgentBackend.ClaudeCode,
        EvaluatorModel = "claude-sonnet-5-5"
    };

    [Fact]
    public void Parser_streams_text_tracks_subagents_and_reads_result()
    {
        var observer = new RecordingObserver();
        var parser = new ClaudeStreamParser(observer);
        string[] lines =
        [
            """{"type":"system","subtype":"init","model":"claude-sonnet-5-5","session_id":"s"}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Checking "}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"the repo."}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Task","input":{"description":"Find tests","subagent_type":"Explore"}}]},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"sub-agent chatter"}},"parent_tool_use_id":"t1"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"dotnet test"}}]},"parent_tool_use_id":"t1"}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"found 3"}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"{\"criteriaMet\": true}","total_cost_usd":0.0421,"num_turns":3,"usage":{"input_tokens":10,"output_tokens":5},"modelUsage":{"claude-sonnet-5-5":{"inputTokens":1200,"outputTokens":300,"cacheReadInputTokens":800,"cacheCreationInputTokens":100}}}""",
            "not json"
        ];
        foreach (var line in lines) parser.ProcessLine(line);

        Assert.True(parser.HasResult);
        Assert.False(parser.IsError);
        Assert.Equal("{\"criteriaMet\": true}", parser.ResultText);
        Assert.Equal("claude-sonnet-5-5", parser.Model);
        Assert.Equal("Checking the repo.", string.Concat(observer.Deltas)); // sub-agent text not streamed
        Assert.Equal(("t1", "Explore"), observer.SubagentsStarted.Single());
        Assert.Equal(("t1", true), observer.SubagentsFinished.Single());
        Assert.Equal(["Task: Find tests"], observer.Tools); // only the main agent's tool calls

        var usage = observer.Usage.Single();
        Assert.Equal(1300, usage.InputTokens); // input + cache creation
        Assert.Equal(800, usage.CacheReadTokens);
        Assert.Equal("claude-sonnet-5-5", usage.Model);
        Assert.Equal((0d, 1L, (double?)null), observer.Billing.Single());
        Assert.Equal(0.0421, observer.Costs.Single());
    }

    [Fact]
    public void Parser_flags_error_results()
    {
        var parser = new ClaudeStreamParser(new RecordingObserver());
        parser.ProcessLine("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":"Reached max turns"}""");
        Assert.True(parser.IsError);
        Assert.Equal("Reached max turns", parser.ResultText);
    }

    [Fact]
    public void Evaluator_and_guide_cannot_edit_files_worker_can()
    {
        var config = Config();
        config.WorkerBackend = AgentBackend.ClaudeCode;
        config.Model = "claude-opus-5-5";
        config.ReasoningEffort = "high";

        var worker = ClaudeCodeAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Worker, "p", TimeSpan.FromMinutes(1)));
        var evaluator = ClaudeCodeAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Evaluator, "p", TimeSpan.FromMinutes(1)));
        var guide = ClaudeCodeAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Guide, "p", TimeSpan.FromMinutes(1), @"C:\repo\web"));

        Assert.DoesNotContain("--disallowed-tools", worker);
        Assert.Contains("--disallowed-tools", evaluator);
        Assert.Contains("--disallowed-tools", guide);

        Assert.Equal("claude-opus-5-5", worker[worker.IndexOf("--model") + 1]);
        Assert.Equal("high", worker[worker.IndexOf("--effort") + 1]);
        Assert.Equal(@"C:\repo\web", worker[worker.IndexOf("--add-dir") + 1]); // rounds see every folder
        Assert.DoesNotContain("--add-dir", guide);                              // a guide turn sees one
        Assert.Contains("--strict-mcp-config", worker);
        Assert.Contains("--no-session-persistence", worker);
    }

    [Fact]
    public void Unsupported_effort_is_not_passed()
    {
        var config = Config();
        config.EvaluatorReasoningEffort = "xhigh";
        var args = ClaudeCodeAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Evaluator, "p", TimeSpan.FromMinutes(1)));
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void Environment_carries_only_the_explicit_token()
    {
        var vars = new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "ambient-key",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "ambient-token",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "desktop",
            ["MY_CLAUDE_TOKEN"] = "explicit"
        };
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        try
        {
            var env = ClaudeCli.BuildEnvironment("MY_CLAUDE_TOKEN", "explicit", @"C:\cfg");

            Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(env.ContainsKey("CLAUDECODE"));
            Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
            Assert.False(env.ContainsKey("MY_CLAUDE_TOKEN"));
            Assert.Equal("explicit", env["CLAUDE_CODE_OAUTH_TOKEN"]);
            Assert.Equal(@"C:\cfg", env["CLAUDE_CONFIG_DIR"]);
            Assert.True(env.ContainsKey("PATH"));
        }
        finally
        {
            foreach (var k in vars.Keys) Environment.SetEnvironmentVariable(k, null);
        }
    }

    [Fact]
    public void Models_are_only_inherited_within_the_same_backend()
    {
        var config = Config(); // worker Copilot gpt-5-mini, evaluator Claude
        config.EvaluatorModel = "";
        Assert.Equal("", config.ModelFor(RoundPhase.Evaluator)); // gpt-5-mini would mean nothing to Claude Code

        config.EvaluatorBackend = AgentBackend.Copilot;
        Assert.Equal("gpt-5-mini", config.ModelFor(RoundPhase.Evaluator));

        config.EvaluatorBackend = AgentBackend.ClaudeCode;
        config.EvaluatorModel = "claude-haiku-5-5";
        config.AgentsMdBackend = AgentsMdBackendChoice.SameAsEvaluator;
        Assert.Equal(AgentBackend.ClaudeCode, config.BackendFor(RoundPhase.Guide));
        Assert.Equal("claude-haiku-5-5", config.ModelFor(RoundPhase.Guide));

        config.AgentsMdBackend = AgentsMdBackendChoice.Copilot;
        Assert.Equal("", config.ModelFor(RoundPhase.Guide));
    }

    [Fact]
    public async Task Router_sends_each_role_to_its_backend_and_starts_only_used_ones()
    {
        var config = Config();
        config.UseAgentsMd = false;
        var copilot = new NamedAgent("copilot");
        var claude = new NamedAgent("claude");
        var router = new RoutingLoopAgent(config, new Dictionary<AgentBackend, Func<ILoopAgent>>
        {
            [AgentBackend.Copilot] = () => copilot,
            [AgentBackend.ClaudeCode] = () => claude
        });

        await router.StartAsync(default);
        var worker = await router.RunTurnAsync(new AgentTurnRequest(RoundPhase.Worker, "p", TimeSpan.FromMinutes(1)), new RecordingObserver(), default);
        var evaluator = await router.RunTurnAsync(new AgentTurnRequest(RoundPhase.Evaluator, "p", TimeSpan.FromMinutes(1)), new RecordingObserver(), default);

        Assert.Equal("copilot", worker);
        Assert.Equal("claude", evaluator);
        Assert.True(copilot.Started && claude.Started);

        config.EvaluatorBackend = AgentBackend.Copilot;
        var claudeOnlyIfUsed = new NamedAgent("claude");
        var copilotOnly = new RoutingLoopAgent(config, new Dictionary<AgentBackend, Func<ILoopAgent>>
        {
            [AgentBackend.Copilot] = () => new NamedAgent("copilot"),
            [AgentBackend.ClaudeCode] = () => claudeOnlyIfUsed
        });
        await copilotOnly.StartAsync(default);
        Assert.False(claudeOnlyIfUsed.Started); // no Claude token needed for an all-Copilot run
    }

    private sealed class NamedAgent(string name) : ILoopAgent
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken) =>
            Task.FromResult(name);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingObserver : IAgentObserver
    {
        public List<string> Deltas { get; } = [];
        public List<string> Tools { get; } = [];
        public List<(string Id, string Name)> SubagentsStarted { get; } = [];
        public List<(string Id, bool Ok)> SubagentsFinished { get; } = [];
        public List<UsageSample> Usage { get; } = [];
        public List<(double, long, double?)> Billing { get; } = [];
        public List<double> Costs { get; } = [];

        public void OnOutputDelta(string text) => Deltas.Add(text);
        public void OnToolStarted(string toolName, string? detail) => Tools.Add(detail ?? toolName);
        public void OnSubagentStarted(string id, string name, string? description) => SubagentsStarted.Add((id, name));
        public void OnSubagentFinished(string id, bool succeeded, string? error) => SubagentsFinished.Add((id, succeeded));
        public void OnUsage(UsageSample usage) => Usage.Add(usage);
        public void OnBilling(double premiumRequests, long userRequests, double? aiCredits) => Billing.Add((premiumRequests, userRequests, aiCredits));
        public void OnEstimatedCost(double usd) => Costs.Add(usd);
        public void OnLog(string level, string message) { }
    }
}
