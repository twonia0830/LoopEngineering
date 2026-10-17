using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

public interface IModelCatalogSource
{
    Task<IReadOnlyList<ModelOption>> ListModelsAsync(string tokenVariable, CancellationToken cancellationToken);
}

/// <summary>
/// Caches the models the account can use so the setup form can offer a dropdown instead of free text.
/// Register as a singleton.
/// </summary>
public sealed class ModelCatalogService(IModelCatalogSource source)
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

    /// <summary>Loads the list unless a fresh one for this token variable is cached (or <paramref name="force"/>). Never throws.</summary>
    public Task RefreshAsync(string tokenVariable, bool force = false)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            if (!force && _loadedFor == tokenVariable && Error is null && LoadedAt is { } at && DateTimeOffset.Now - at < CacheDuration)
                return Task.CompletedTask;

            IsLoading = true;
            _inFlight = LoadAsync(tokenVariable);
        }
        Notify();
        return _inFlight;
    }

    private async Task LoadAsync(string tokenVariable)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var models = await source.ListModelsAsync(tokenVariable, timeout.Token);
            lock (_gate)
            {
                Models = models;
                LoadedAt = DateTimeOffset.Now;
                Error = null;
                _loadedFor = tokenVariable;
            }
        }
        catch (Exception ex)
        {
            lock (_gate) Error = ex.Message; // keep any previously loaded list
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
