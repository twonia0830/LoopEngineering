using System.Text;
using System.Text.Json;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Antigravity;

/// <summary>
/// Turns `agy --output-format stream-json` events (init, step_update, result) into observer events:
/// streamed text, tool calls, sub-agents, and the final result with usage.
/// </summary>
public sealed class AntigravityStreamParser(IAgentObserver observer)
{
    /// <summary>Tools agy uses to run a sub-agent.</summary>
    private static readonly HashSet<string> SubagentTools = new(StringComparer.Ordinal) { "invoke_subagent", "browser_subagent" };

    private readonly HashSet<int> _toolStepsStarted = [];
    private readonly HashSet<string> _openSubagents = [];
    private readonly StringBuilder _streamed = new();

    public bool HasResult { get; private set; }
    public bool IsError { get; private set; }
    public string ResultText { get; private set; } = "";
    public string? Error { get; private set; }
    public string? Model { get; private set; }

    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            switch (Str(root, "event"))
            {
                case "init" when root.TryGetProperty("init", out var init):
                    Model = Str(init, "model");
                    break;
                case "step_update" when root.TryGetProperty("step_update", out var step):
                    HandleStep(step);
                    break;
                case "result" when root.TryGetProperty("result", out var result):
                    HandleResult(result);
                    break;
            }
        }
    }

    private void HandleStep(JsonElement step)
    {
        var type = Str(step, "step_type");
        var state = Str(step, "state");
        var index = step.TryGetProperty("step_index", out var i) && i.TryGetInt32(out var n) ? n : -1;

        if (type == "agent_response")
        {
            var text = Str(step, "text_delta");
            if (!string.IsNullOrEmpty(text))
            {
                _streamed.Append(text);
                observer.OnOutputDelta(text);
            }
            return;
        }

        if (type != "tool") return;
        var tool = Str(step, "tool_name") ?? "tool";
        step.TryGetProperty("tool_info", out var info);
        info.TryGetProperty("parameters", out var parameters);
        var key = $"step-{index}";

        // A tool step is reported as ACTIVE and later DONE; announce it once.
        if (_toolStepsStarted.Add(index))
        {
            if (SubagentTools.Contains(tool))
            {
                _openSubagents.Add(key);
                observer.OnSubagentStarted(key, FirstString(parameters, "Name", "name", "Agent", "agent", "Task", "task") ?? tool, null);
            }
            observer.OnToolStarted(tool, Summarize(tool, parameters));
        }

        if (state is "DONE" or "ERROR" or "FAILED" && _openSubagents.Remove(key))
            observer.OnSubagentFinished(key, state == "DONE", state == "DONE" ? null : $"Sub-agent {state.ToLowerInvariant()}");
    }

    private void HandleResult(JsonElement result)
    {
        HasResult = true;
        IsError = Str(result, "status") is { } status && status != "SUCCESS";
        Error = Str(result, "error");
        var text = Str(result, "response");
        ResultText = string.IsNullOrWhiteSpace(text) ? _streamed.ToString() : text;

        if (result.TryGetProperty("usage", out var usage))
        {
            observer.OnUsage(new UsageSample(
                Num(usage, "input_tokens"),
                Num(usage, "output_tokens"),
                Num(usage, "cache_read_tokens"),
                Num(usage, "thinking_tokens"),
                Model));
        }

        // One prompt per turn; Google AI plans are not billed in Copilot credits, and agy reports no cost.
        observer.OnBilling(0, 1, null);

        foreach (var key in _openSubagents) observer.OnSubagentFinished(key, false, "Turn ended");
        _openSubagents.Clear();
    }

    private static string? Summarize(string tool, JsonElement parameters)
    {
        var detail = FirstString(parameters, "CommandLine", "Command", "command", "Path", "path", "AbsolutePath", "Query", "query", "Name", "name");
        if (detail is null) return tool;
        detail = detail.ReplaceLineEndings(" ");
        return $"{tool}: {(detail.Length > 80 ? detail[..80] + "…" : detail)}";
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            if (Str(obj, name) is { Length: > 0 } value) return value;
        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
