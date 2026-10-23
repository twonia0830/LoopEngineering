namespace LoopEngineering.Core.Models;

/// <summary>A model the account can use, as listed by the Copilot runtime.</summary>
public sealed record ModelOption(
    string Id,
    string Name,
    string? PolicyState,
    IReadOnlyList<string> ReasoningEfforts,
    string? DefaultReasoningEffort,
    double? InputCreditsPerMillion,
    double? CachedInputCreditsPerMillion,
    double? OutputCreditsPerMillion,
    string PriceUnit = "ai_credits")
{
    /// <summary>True when the prices are API-equivalent USD (Claude Code) rather than Copilot AI credits.</summary>
    public bool PricesInUsd => PriceUnit == "usd";

    /// <summary>The runtime's automatic model choice.</summary>
    public bool IsAuto => Id == "auto";

    /// <summary>False when an org/enterprise policy (or terms not yet accepted) blocks the model.</summary>
    public bool IsEnabled => PolicyState is null or "enabled";

    public bool HasPrices => InputCreditsPerMillion is not null && OutputCreditsPerMillion is not null;

    /// <summary>e.g. "200 / 1,000" (input / output AI credits per 1M tokens).</summary>
    public string PriceLabel => !HasPrices ? ""
        : PricesInUsd ? $"${InputCreditsPerMillion:#,0.##} / ${OutputCreditsPerMillion:#,0.##}"
        : $"{InputCreditsPerMillion:#,0.##} / {OutputCreditsPerMillion:#,0.##}";
}
