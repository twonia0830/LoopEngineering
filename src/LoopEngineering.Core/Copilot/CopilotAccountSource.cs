using GitHub.Copilot;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

// The session quota RPC and its types are marked experimental (GHCP001) in the SDK preview.
#pragma warning disable GHCP001

namespace LoopEngineering.Core.Copilot;

/// <summary>
/// Account-level reads that need no LLM request and cost nothing: the quota (through a long-lived,
/// idle session's quota RPC, which understands credit billing) and the list of usable models with
/// their prices. The runtime and session are kept alive (restarted if the token variable changes)
/// so repeated reads are cheap.
/// </summary>
public sealed class CopilotAccountSource : IAccountQuotaSource, IModelCatalogSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CopilotClient? _client;
    private CopilotSession? _session;
    private string? _clientTokenVariable;

    public Task<IReadOnlyList<AccountQuota>> FetchAsync(string tokenVariable, CancellationToken cancellationToken) =>
        UseClientAsync(tokenVariable, needSession: true, async (client, session) =>
        {
            var result = await session!.Rpc.Quota.RefreshAsync(cancellationToken);
            return Map(result.ProviderQuotas, result.Snapshots);
        }, cancellationToken);

    public Task<IReadOnlyList<ModelOption>> ListModelsAsync(string tokenVariable, CancellationToken cancellationToken) =>
        UseClientAsync(tokenVariable, needSession: false, async (client, _) =>
        {
            var models = await client.ListModelsAsync(cancellationToken);

            // Prices are per billing batch in a per-model unit; normalize to AI credits per 1M tokens.
            var units = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var list = await client.Rpc.Models.ListTokenPriceUnitsAsync(cancellationToken);
                foreach (var u in list.Units ?? []) units[u.ModelId] = u.Unit.Value ?? "";
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Without units the prices can't be interpreted; the models are still listed.
            }

            return (IReadOnlyList<ModelOption>)models.Select(m => ToOption(m, units.GetValueOrDefault(m.Id))).ToList();
        }, cancellationToken);

    internal static ModelOption ToOption(ModelInfo model, string? unit)
    {
        var prices = model.Billing?.TokenPrices;
        double? Normalize(double? price)
        {
            if (price is null || prices?.BatchSize is not > 0) return null;
            var perMillion = price.Value * 1_000_000d / prices.BatchSize.Value;
            return unit switch
            {
                "ai_credits" => perMillion,
                "nano_aiu" => perMillion / 1_000_000_000d,
                _ => null
            };
        }

        return new ModelOption(
            model.Id,
            string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
            model.Policy?.State,
            model.SupportedReasoningEfforts?.ToList() ?? [],
            model.DefaultReasoningEffort,
            Normalize(prices?.InputPrice),
            Normalize(prices?.CacheReadPrice ?? prices?.CachePrice),
            Normalize(prices?.OutputPrice));
    }

    private async Task<T> UseClientAsync<T>(string tokenVariable, bool needSession,
        Func<CopilotClient, CopilotSession?, Task<T>> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_client is null || _clientTokenVariable != tokenVariable)
            {
                await ResetAsync();
                _client = await CopilotClients.StartAsync(tokenVariable, workingDirectory: null, cancellationToken);
                _clientTokenVariable = tokenVariable;
            }
            if (needSession && _session is null)
            {
                _session = await _client.CreateSessionAsync(new SessionConfig
                {
                    OnPermissionRequest = PermissionHandler.ApproveAll,
                    EnableConfigDiscovery = false
                }, cancellationToken);
            }

            try
            {
                return await action(_client, _session);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The runtime or session may have died; start fresh next time.
                await ResetAsync();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Prefers the provider budgets (unit-aware); falls back to the legacy snapshot map.</summary>
    internal static IReadOnlyList<AccountQuota> Map(
        IList<ProviderQuotaState>? providerQuotas,
        IDictionary<string, GitHub.Copilot.Rpc.SessionQuotaSnapshot>? snapshots)
    {
        var quotas = new List<AccountQuota>();
        foreach (var state in providerQuotas ?? [])
        {
            if (state.BudgetMetadata is not { } budget) continue;
            quotas.Add(new AccountQuota(
                state.QuotaId ?? state.CompatibilityKey ?? "quota",
                budget.Consumed,
                budget.Entitlement,
                budget.Unlimited,
                budget.RemainingPercentage,
                budget.Overage,
                ToDate(budget.ResetAtEpochMs),
                state.Unit.Value ?? "requests"));
        }

        if (quotas.Count == 0)
        {
            foreach (var (name, snap) in snapshots ?? new Dictionary<string, GitHub.Copilot.Rpc.SessionQuotaSnapshot>())
            {
                quotas.Add(new AccountQuota(
                    name,
                    snap.UsedRequests,
                    snap.EntitlementRequests,
                    snap.IsUnlimitedEntitlement || snap.EntitlementRequests < 0,
                    snap.RemainingPercentage,
                    snap.Overage,
                    ToDate(snap.ResetDateEpochMs),
                    snap.TokenBasedBilling == true ? "ai_credits" : "requests"));
            }
        }

        return quotas
            .OrderBy(q => q.Name == "premium_interactions" ? 0 : 1)
            .ThenBy(q => q.Name)
            .ToList();
    }

    private static DateTimeOffset? ToDate(double? epochMs) =>
        epochMs is { } ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    private async Task ResetAsync()
    {
        var session = _session;
        var client = _client;
        _session = null;
        _client = null;
        _clientTokenVariable = null;

        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch { /* runtime may already be gone */ }
        }
        if (client is not null) await CopilotClients.StopQuietlyAsync(client);
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try { await ResetAsync(); }
        finally { _lock.Release(); }
    }
}
