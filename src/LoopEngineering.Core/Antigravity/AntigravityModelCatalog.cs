using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Antigravity;

/// <summary>Caches `agy models` for the setup form. Register as a singleton.</summary>
public sealed class AntigravityModelCatalog
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private Task? _inFlight;
    private string? _loadedFor;

    public IReadOnlyList<ModelOption> Models { get; private set; } = [];
    public DateTimeOffset? LoadedAt { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading { get; private set; }

    public event Action? Changed;

    public ModelOption? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads the list unless a fresh one for the same CLI settings is cached. Never throws.</summary>
    public Task RefreshAsync(string cliPathSetting, bool isolate, bool force = false)
    {
        var key = $"{cliPathSetting}|{isolate}";
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            if (!force && _loadedFor == key && Error is null && LoadedAt is { } at && DateTimeOffset.Now - at < CacheDuration)
                return Task.CompletedTask;
            IsLoading = true;
            _inFlight = LoadAsync(cliPathSetting, isolate, key);
        }
        Notify();
        return _inFlight;
    }

    private async Task LoadAsync(string cliPathSetting, bool isolate, string key)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var path = AntigravityCli.ResolvePath(cliPathSetting);
            var models = await AntigravityCli.ListModelsAsync(path, isolate, timeout.Token);
            lock (_gate)
            {
                Models = models;
                LoadedAt = DateTimeOffset.Now;
                Error = null;
                _loadedFor = key;
            }
        }
        catch (Exception ex)
        {
            lock (_gate) Error = ex.Message;
        }
        finally
        {
            lock (_gate) IsLoading = false;
            Notify();
        }
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* UI subscriber errors are not ours */ }
    }
}
