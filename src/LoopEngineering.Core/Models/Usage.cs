namespace LoopEngineering.Core.Models;

/// <summary>Usage reported by a single LLM API call (one assistant.usage event).</summary>
public sealed record UsageSample(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long ReasoningTokens,
    string? Model);

/// <summary>Accumulated token usage for a round or a whole run. Sub-agent calls are included.</summary>
public sealed class TokenUsage
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long ReasoningTokens { get; set; }

    /// <summary>
    /// Premium requests as GitHub bills them: user-initiated prompts × model multiplier, as reported by the
    /// runtime per session. Tool-loop follow-up calls and sub-agent calls are not billed separately.
    /// </summary>
    public double PremiumRequests { get; set; }

    /// <summary>Number of user-initiated (billable) prompts.</summary>
    public long UserRequests { get; set; }

    /// <summary>AI credits charged, for accounts on credit (token-based) billing. Null until the runtime reports any.</summary>
    public double? AiCredits { get; set; }

    public int ApiCalls { get; set; }

    /// <summary>Models that actually served the calls (as reported by the runtime), with call counts.</summary>
    public Dictionary<string, int> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long TotalTokens => InputTokens + OutputTokens;

    public bool IsEmpty => ApiCalls == 0 && UserRequests == 0;

    public void Add(UsageSample sample)
    {
        InputTokens += sample.InputTokens;
        OutputTokens += sample.OutputTokens;
        CacheReadTokens += sample.CacheReadTokens;
        ReasoningTokens += sample.ReasoningTokens;
        ApiCalls++;
        if (!string.IsNullOrWhiteSpace(sample.Model)) Models[sample.Model] = Models.GetValueOrDefault(sample.Model) + 1;
    }

    public void AddBilling(double premiumRequests, long userRequests, double? aiCredits)
    {
        PremiumRequests += premiumRequests;
        UserRequests += userRequests;
        if (aiCredits is { } credits) AiCredits = (AiCredits ?? 0) + credits;
    }

    public void Add(TokenUsage other)
    {
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CacheReadTokens += other.CacheReadTokens;
        ReasoningTokens += other.ReasoningTokens;
        PremiumRequests += other.PremiumRequests;
        UserRequests += other.UserRequests;
        if (other.AiCredits is { } credits) AiCredits = (AiCredits ?? 0) + credits;
        ApiCalls += other.ApiCalls;
        foreach (var (model, calls) in other.Models) Models[model] = Models.GetValueOrDefault(model) + calls;
    }

    public void Reset()
    {
        InputTokens = OutputTokens = CacheReadTokens = ReasoningTokens = UserRequests = 0;
        PremiumRequests = 0;
        AiCredits = null;
        ApiCalls = 0;
        Models.Clear();
    }

    public TokenUsage Clone()
    {
        var copy = (TokenUsage)MemberwiseClone();
        copy.Models = new Dictionary<string, int>(Models, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>e.g. "gpt-5-mini ×5".</summary>
    public string ModelsLabel => string.Join(", ", Models.OrderByDescending(m => m.Value).Select(m => $"{m.Key} ×{m.Value}"));

    public static TokenUsage Sum(IEnumerable<TokenUsage> items)
    {
        var total = new TokenUsage();
        foreach (var item in items) total.Add(item);
        return total;
    }

    /// <summary>Compact number: 950, 12.3k, 4.56M.</summary>
    public static string Format(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000d:0.##}M",
        >= 10_000 => $"{n / 1_000d:0.#}k",
        _ => n.ToString("N0")
    };

    public override string ToString() =>
        $"in {Format(InputTokens)} / out {Format(OutputTokens)} (cache read {Format(CacheReadTokens)}), " +
        $"{ApiCalls} API calls, {PremiumRequests:0.##} premium requests ({UserRequests} prompts)" +
        (AiCredits is { } credits ? $", {credits:0.###} AI credits" : "");
}

/// <summary>One quota bucket of the Copilot account, e.g. "premium_interactions".</summary>
public sealed record AccountQuota(
    string Name,
    double Used,
    double Entitlement,
    bool IsUnlimited,
    double RemainingPercentage,
    double Overage,
    DateTimeOffset? ResetDate,
    string Unit = "requests")
{
    /// <summary>True for accounts on credit (token-based) billing, where the budget is AI credits.</summary>
    public bool IsCredits => Unit == "ai_credits";

    public string DisplayName => Name switch
    {
        _ when IsCredits => "Credits",
        "premium_interactions" => "Premium requests",
        "chat" => "Chat",
        "completions" => "Completions",
        _ => Name
    };
}

public sealed class AccountQuotaSnapshot
{
    public DateTimeOffset? FetchedAt { get; init; }
    public IReadOnlyList<AccountQuota> Quotas { get; init; } = [];
    public string? Error { get; init; }
    public bool IsRefreshing { get; init; }

    /// <summary>The bucket worth showing first: premium requests, else the first limited one.</summary>
    public AccountQuota? Primary =>
        Quotas.FirstOrDefault(q => q.Name == "premium_interactions")
        ?? Quotas.FirstOrDefault(q => !q.IsUnlimited)
        ?? Quotas.FirstOrDefault();
}
