using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

public interface IAccountQuotaSource
{
    Task<IReadOnlyList<AccountQuota>> FetchAsync(string tokenVariable, CancellationToken cancellationToken);
}

/// <summary>
/// Holds the latest account quota snapshot for the UI. Refreshes on demand and after each loop round,
/// with automatic refreshes throttled so a fast loop does not hammer the API. Register as a singleton.
/// </summary>
public sealed class AccountQuotaService(IAccountQuotaSource source)
{
    public static readonly TimeSpan MinAutoRefreshInterval = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private AccountQuotaSnapshot _current = new();
    private Task? _inFlight;
    private string? _tokenVariable;

    public event Action? Changed;

    public AccountQuotaSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>The token variable used by automatic refreshes; set from the UI config.</summary>
    public string? TokenVariable
    {
        get { lock (_gate) return _tokenVariable; }
        set { lock (_gate) _tokenVariable = value; }
    }

    /// <summary>
    /// Refreshes the snapshot. <paramref name="force"/> = false skips the call if the last fetch is recent.
    /// Concurrent callers share one in-flight request. Never throws; errors land in <see cref="AccountQuotaSnapshot.Error"/>.
    /// </summary>
    public Task RefreshAsync(bool force = true)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            if (string.IsNullOrWhiteSpace(_tokenVariable)) return Task.CompletedTask;
            if (!force && _current.FetchedAt is { } last && DateTimeOffset.Now - last < MinAutoRefreshInterval)
                return Task.CompletedTask;

            _current = new AccountQuotaSnapshot
            {
                FetchedAt = _current.FetchedAt,
                Quotas = _current.Quotas,
                Error = _current.Error,
                IsRefreshing = true
            };
            _inFlight = FetchAsync(_tokenVariable);
        }
        Notify();
        return _inFlight;
    }

    private async Task FetchAsync(string tokenVariable)
    {
        AccountQuotaSnapshot next;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var quotas = await source.FetchAsync(tokenVariable, timeout.Token);
            next = new AccountQuotaSnapshot { FetchedAt = DateTimeOffset.Now, Quotas = quotas };
        }
        catch (Exception ex)
        {
            AccountQuotaSnapshot previous;
            lock (_gate) previous = _current;
            // Keep showing the last good numbers next to the error.
            next = new AccountQuotaSnapshot { FetchedAt = previous.FetchedAt, Quotas = previous.Quotas, Error = ex.Message };
        }

        lock (_gate) _current = next;
        Notify();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* UI subscriber errors are not ours */ }
    }
}
