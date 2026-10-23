using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.ClaudeCode;

/// <summary>
/// Models offered for the Claude Code backend. Without an Anthropic API key there is no model-list endpoint
/// to ask, so the current lineup is listed here. Prices are Anthropic API rates (USD per 1M tokens), shown as
/// an API-equivalent guide: on a Claude subscription, usage counts against the plan's limits instead.
/// </summary>
public static class ClaudeCodeModels
{
    private static readonly string[] Efforts = ["low", "medium", "high", "max"];

    public static IReadOnlyList<ModelOption> All { get; } =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", null, Efforts, null, 4, 0.2, 20, "usd"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", null, Efforts, null, 2, 0.2, 10, "usd"),
        new("claude-haiku-5-5", "Claude Haiku 5.5", null, Efforts, null, 0.1, null, 0.5, "usd"),
        new("claude-fable-5-1", "Claude Fable 5.1 (if your plan includes it)", null, Efforts, null, 10, 0.25, 50, "usd")
    ];

    public static ModelOption? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
