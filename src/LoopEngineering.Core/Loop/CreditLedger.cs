using System.Text.Json;

namespace LoopEngineering.Core.Loop;

/// <summary>
/// Running total of AI credits this app has spent in the current billing month, persisted to disk so it
/// survives restarts. Needed because the SDK's account quota reports 0 consumed for credit-billed
/// accounts. Copilot budgets reset at the start of each calendar month (UTC). Register as a singleton.
/// </summary>
public sealed class CreditLedger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private LedgerData _data;

    public CreditLedger(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _data = Load();
    }

    public event Action? Changed;

    /// <summary>Credits spent by this app in the current billing month.</summary>
    public double CurrentMonthCredits
    {
        get
        {
            lock (_gate) return _data.Period == CurrentPeriod() ? _data.Credits : 0;
        }
    }

    /// <summary>Start of the next billing month, in UTC.</summary>
    public DateTimeOffset NextReset
    {
        get
        {
            var now = _clock().ToUniversalTime();
            return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        }
    }

    public void Add(double credits)
    {
        if (credits <= 0) return;
        lock (_gate)
        {
            var period = CurrentPeriod();
            if (_data.Period != period) _data = new LedgerData { Period = period };
            _data.Credits += credits;
            Save();
        }
        try { Changed?.Invoke(); }
        catch { /* UI subscriber errors are not ours */ }
    }

    private string CurrentPeriod() => _clock().ToUniversalTime().ToString("yyyy-MM");

    private LedgerData Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<LedgerData>(File.ReadAllText(_path)) ?? new LedgerData();
        }
        catch (JsonException)
        {
            // corrupt file: start over
        }
        return new LedgerData();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, Options));
        }
        catch (IOException)
        {
            // the in-memory total still works; persistence is best effort
        }
    }

    private sealed class LedgerData
    {
        public string Period { get; set; } = "";
        public double Credits { get; set; }
    }
}
