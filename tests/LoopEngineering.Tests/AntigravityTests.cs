using System.Text.Json;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Antigravity;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Tests;

public sealed class AntigravityTests
{
    [Fact]
    public void Parser_reads_real_agy_stream()
    {
        // Captured from `agy --output-format stream-json` (1.4.6), trimmed.
        string[] lines =
        [
            """{"event":"init","conversation_id":"a01d","init":{"model":"gemini-3.8-flash-low","cwd":"C:\\probe","tools":["read_file","invoke_subagent"]}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":0,"state":"DONE","step_type":"user_input"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":4.0,"usage":{"input_tokens":6899,"output_tokens":83,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":6982}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"read_file","tool_info":{"name":"read_file","parameters":{"Path":"C:\\probe\\note.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":2,"state":"DONE","step_type":"tool","tool_name":"read_file","tool_info":{"name":"read_file","parameters":{"Path":"C:\\probe\\note.txt"},"output":"1 lines, 31 bytes"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":3,"state":"ACTIVE","step_type":"tool","tool_name":"invoke_subagent","tool_info":{"name":"invoke_subagent","parameters":{"Name":"test-runner"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":3,"state":"DONE","step_type":"tool","tool_name":"invoke_subagent","tool_info":{"name":"invoke_subagent","parameters":{"Name":"test-runner"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":4,"state":"ACTIVE","step_type":"agent_response","text_delta":"pineapple"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"a01d","step_index":4,"state":"DONE","step_type":"agent_response","text_delta":"\n"}}""",
            """{"event":"result","result":{"conversation_id":"a01d","status":"SUCCESS","response":"pineapple\n","duration_seconds":6.5,"num_turns":1,"usage":{"input_tokens":13972,"output_tokens":84,"thinking_tokens":12,"cache_read_tokens":500,"total_tokens":14056}}}"""
        ];
        var observer = new RecordingObserver();
        var parser = new AntigravityStreamParser(observer);
        foreach (var line in lines) parser.ProcessLine(line);

        Assert.True(parser.HasResult);
        Assert.False(parser.IsError);
        Assert.Equal("pineapple\n", parser.ResultText);
        Assert.Equal("pineapple\n", string.Concat(observer.Deltas));
        Assert.Equal(["read_file: C:\\probe\\note.txt", "invoke_subagent: test-runner"], observer.Tools); // each tool step announced once
        Assert.Equal(("step-3", "test-runner"), observer.SubagentsStarted.Single());
        Assert.Equal(("step-3", true), observer.SubagentsFinished.Single());

        var usage = observer.Usage.Single();
        Assert.Equal(13972, usage.InputTokens);
        Assert.Equal(84, usage.OutputTokens);
        Assert.Equal(500, usage.CacheReadTokens);
        Assert.Equal(12, usage.ReasoningTokens);
        Assert.Equal("gemini-3.8-flash-low", usage.Model);
        Assert.Equal((0d, 1L, (double?)null), observer.Billing.Single());
    }

    [Fact]
    public void Parser_reports_error_results()
    {
        var parser = new AntigravityStreamParser(new RecordingObserver());
        parser.ProcessLine("""{"event":"result","result":{"status":"ERROR","response":"","error":"authentication required"}}""");
        Assert.True(parser.IsError);
        Assert.Equal("authentication required", parser.Error);
    }

    [Fact]
    public void Stdin_input_is_one_stream_json_user_event()
    {
        var line = AntigravityAgent.BuildInput("Fix the \"bug\"\nnow");
        Assert.EndsWith("\n", line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("user", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Fix the \"bug\"\nnow", doc.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Arguments_use_stdin_input_model_and_extra_folders()
    {
        var config = new LoopConfig
        {
            WorkingDirectories = [@"C:\repo\api", @"C:\repo\web"],
            WorkerBackend = AgentBackend.Antigravity,
            Model = "gemini-4-argon-high"
        };
        var worker = AntigravityAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Worker, "p", TimeSpan.FromMinutes(1)));
        var guide = AntigravityAgent.BuildArguments(config, new AgentTurnRequest(RoundPhase.Guide, "p", TimeSpan.FromMinutes(1), @"C:\repo\web"));

        Assert.Equal("stream-json", worker[worker.IndexOf("--input-format") + 1]);
        Assert.Equal("gemini-4-argon-high", worker[worker.IndexOf("--model") + 1]);
        Assert.Equal(@"C:\repo\web", worker[worker.IndexOf("--add-dir") + 1]);
        Assert.DoesNotContain("--add-dir", guide);
        Assert.DoesNotContain("-p", worker); // -p would take the next flag as its prompt
    }

    [Fact]
    public void Parses_agy_models_output()
    {
        var models = AntigravityCli.ParseModels("Fetching available models...\ngemini-4-argon-high\tGemini 4 Argon (High)\r\nclaude-opus-5-5-low\tClaude Opus 5.5 (Low)\n");
        Assert.Equal(["gemini-4-argon-high", "claude-opus-5-5-low"], models.Select(m => m.Id));
        Assert.Equal("Gemini 4 Argon (High)", models[0].Name);
        Assert.Empty(models[0].ReasoningEfforts); // effort is part of the variant
    }

    [Fact]
    public void Isolated_environment_moves_home_but_keeps_tool_locations()
    {
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "would-switch-to-api-billing");
        try
        {
            var realProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var env = AntigravityCli.BuildEnvironment(isolate: true, @"C:\iso-home");

            Assert.Equal(@"C:\iso-home", env["USERPROFILE"]);
            Assert.Equal(@"C:\iso-home", env["HOME"]);
            Assert.False(env.ContainsKey("GEMINI_API_KEY"));
            Assert.Equal(realProfile, env["DOTNET_CLI_HOME"]);
            Assert.StartsWith(realProfile, env["NUGET_PACKAGES"]);

            var plain = AntigravityCli.BuildEnvironment(isolate: false, @"C:\iso-home");
            Assert.NotEqual(@"C:\iso-home", plain.GetValueOrDefault("USERPROFILE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        }
    }

    [Fact]
    public void Guide_can_follow_or_override_the_evaluator_backend()
    {
        var config = new LoopConfig { EvaluatorBackend = AgentBackend.Antigravity, EvaluatorModel = "gemini-3.8-flash-low" };
        Assert.Equal(AgentBackend.Antigravity, config.BackendFor(RoundPhase.Guide));
        Assert.Equal("gemini-3.8-flash-low", config.ModelFor(RoundPhase.Guide));
        Assert.True(config.UsesBackend(AgentBackend.Antigravity));

        config.AgentsMdBackend = AgentsMdBackendChoice.Antigravity;
        config.EvaluatorBackend = AgentBackend.ClaudeCode;
        Assert.Equal(AgentBackend.Antigravity, config.BackendFor(RoundPhase.Guide));
        Assert.Equal("", config.ModelFor(RoundPhase.Guide)); // evaluator's Claude model doesn't carry over
    }

    private sealed class RecordingObserver : IAgentObserver
    {
        public List<string> Deltas { get; } = [];
        public List<string> Tools { get; } = [];
        public List<(string Id, string Name)> SubagentsStarted { get; } = [];
        public List<(string Id, bool Ok)> SubagentsFinished { get; } = [];
        public List<UsageSample> Usage { get; } = [];
        public List<(double, long, double?)> Billing { get; } = [];

        public void OnOutputDelta(string text) => Deltas.Add(text);
        public void OnToolStarted(string toolName, string? detail) => Tools.Add(detail ?? toolName);
        public void OnSubagentStarted(string id, string name, string? description) => SubagentsStarted.Add((id, name));
        public void OnSubagentFinished(string id, bool succeeded, string? error) => SubagentsFinished.Add((id, succeeded));
        public void OnUsage(UsageSample usage) => Usage.Add(usage);
        public void OnBilling(double premiumRequests, long userRequests, double? aiCredits) => Billing.Add((premiumRequests, userRequests, aiCredits));
        public void OnEstimatedCost(double usd) { }
        public void OnLog(string level, string message) { }
    }
}
