using System.Text;
using System.Text.Json;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.ClaudeCode;

/// <summary>
/// Turns Claude Code's `--output-format stream-json` lines into observer events: streamed text, tool calls,
/// sub-agents (Task tool), and the final result with usage and cost.
/// </summary>
public sealed class ClaudeStreamParser(IAgentObserver observer)
{
    /// <summary>Tool names Claude Code uses to launch a sub-agent.</summary>
    private static readonly HashSet<string> SubagentTools = new(StringComparer.Ordinal) { "Task", "Agent" };

    private readonly HashSet<string> _openSubagents = [];
    private readonly StringBuilder _streamed = new();

    public bool HasResult { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>The final reply (the result event's text, or the streamed text if the result had none).</summary>
    public string ResultText { get; private set; } = "";

    public string? Model { get; private set; }

    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; } // not an event line (e.g. a stray log)

        using (doc)
        {
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "system" when Str(root, "subtype") == "init":
                    Model = Str(root, "model");
                    break;
                case "stream_event" when IsTopLevel(root):
                    HandleStreamEvent(root);
                    break;
                case "assistant" when IsTopLevel(root):
                    HandleAssistant(root);
                    break;
                case "user":
                    HandleToolResults(root);
                    break;
                case "result":
                    HandleResult(root);
                    break;
            }
        }
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || Str(ev, "type") != "content_block_delta") return;
        if (!ev.TryGetProperty("delta", out var delta) || Str(delta, "type") != "text_delta") return;
        var text = Str(delta, "text");
        if (string.IsNullOrEmpty(text)) return;
        _streamed.Append(text);
        observer.OnOutputDelta(text);
    }

    private void HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_use") continue;
            var name = Str(block, "name") ?? "tool";
            var id = Str(block, "id") ?? Guid.NewGuid().ToString("N");
            block.TryGetProperty("input", out var input);

            if (SubagentTools.Contains(name))
            {
                _openSubagents.Add(id);
                observer.OnSubagentStarted(id,
                    Str(input, "subagent_type") ?? Str(input, "description") ?? "subagent",
                    Str(input, "description"));
            }
            observer.OnToolStarted(name, Summarize(name, input));
        }
    }

    private void HandleToolResults(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") != "tool_result") continue;
            var id = Str(block, "tool_use_id");
            if (id is null || !_openSubagents.Remove(id)) continue;
            var failed = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            observer.OnSubagentFinished(id, !failed, failed ? "Sub-agent reported an error" : null);
        }
    }

    private void HandleResult(JsonElement root)
    {
        HasResult = true;
        IsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True
                  || Str(root, "subtype") is { } subtype && subtype != "success";
        var text = Str(root, "result");
        ResultText = string.IsNullOrWhiteSpace(text) ? _streamed.ToString() : text;

        // Per-model totals when available, otherwise the turn's overall usage under the session model.
        if (root.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object && models.EnumerateObject().Any())
        {
            foreach (var model in models.EnumerateObject())
            {
                observer.OnUsage(new UsageSample(
                    Num(model.Value, "inputTokens") + Num(model.Value, "cacheCreationInputTokens"),
                    Num(model.Value, "outputTokens"),
                    Num(model.Value, "cacheReadInputTokens"),
                    0,
                    model.Name));
            }
        }
        else if (root.TryGetProperty("usage", out var usage))
        {
            observer.OnUsage(new UsageSample(
                Num(usage, "input_tokens") + Num(usage, "cache_creation_input_tokens"),
                Num(usage, "output_tokens"),
                Num(usage, "cache_read_input_tokens"),
                0,
                Model));
        }

        // One prompt per turn; Claude subscriptions are not billed in Copilot credits or premium requests.
        observer.OnBilling(0, 1, null);
        if (root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDouble(out var usd))
            observer.OnEstimatedCost(usd);

        // Anything still open never reported back before the turn ended.
        foreach (var id in _openSubagents) observer.OnSubagentFinished(id, false, "Turn ended");
        _openSubagents.Clear();
    }

    /// <summary>Events from sub-agents carry parent_tool_use_id; only the main agent's text is streamed.</summary>
    private static bool IsTopLevel(JsonElement root) =>
        !root.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null;

    private static string? Summarize(string tool, JsonElement input)
    {
        var detail = Str(input, "description") ?? Str(input, "command") ?? Str(input, "file_path") ?? Str(input, "pattern");
        if (detail is null) return tool;
        detail = detail.ReplaceLineEndings(" ");
        return $"{tool}: {(detail.Length > 80 ? detail[..80] + "…" : detail)}";
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
