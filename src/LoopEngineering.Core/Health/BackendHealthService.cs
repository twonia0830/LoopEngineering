using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Antigravity;
using LoopEngineering.Core.ClaudeCode;
using LoopEngineering.Core.Copilot;
using LoopEngineering.Core.Loop;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Health;

/// <summary>Setup and verification state of one backend.</summary>
public sealed record BackendStatus(
    AgentBackend Backend,
    bool IsConfigured,
    string SetupDetail,
    bool IsVerified,
    DateTimeOffset? VerifiedAt = null,
    string? VerifiedModel = null,
    string? LastError = null,
    bool IsChecking = false,
    bool IsVerifying = false)
{
    /// <summary>Only backends that are set up and verified can be picked for a role.</summary>
    public bool IsUsable => IsConfigured && IsVerified;
}

/// <summary>
/// Checks whether each backend is set up (free: token variable present, CLI found, signed in) and verified
/// (a minimal prompt on the cheapest model succeeded). Verification is remembered per credential
/// fingerprint, a hash, never the token itself, and is dropped when the credential changes.
/// Register as a singleton.
/// </summary>
public sealed class BackendHealthService(
    string storePath,
    ModelCatalogService copilotModels,
    AntigravityModelCatalog antigravityModels,
    Func<AgentBackend, LoopConfig, ILoopAgent>? agentFactory = null)
{
    public const string VerifyPrompt = "This is a connectivity check. Reply with exactly the word OK and nothing else. Do not use any tools.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly Dictionary<AgentBackend, BackendStatus> _status =
        Enum.GetValues<AgentBackend>().ToDictionary(b => b, b => new BackendStatus(b, false, "Not checked yet", false));
    private readonly Dictionary<AgentBackend, string> _fingerprints = [];
    private readonly Func<AgentBackend, LoopConfig, ILoopAgent> _agentFactory = agentFactory ?? CreateAgent;

    public event Action? Changed;

    public IReadOnlyList<BackendStatus> Statuses
    {
        get { lock (_gate) return Enum.GetValues<AgentBackend>().Select(b => _status[b]).ToList(); }
    }

    public BackendStatus Get(AgentBackend backend)
    {
        lock (_gate) return _status[backend];
    }

    public bool IsUsable(AgentBackend backend) => Get(backend).IsUsable;

    // ---------- setup checks (free) ----------

    /// <summary>Runs the free setup checks for every backend and applies remembered verifications.</summary>
    public async Task CheckAllAsync(LoopConfig settings, CancellationToken cancellationToken = default)
    {
        foreach (var backend in Enum.GetValues<AgentBackend>()) Update(backend, s => s with { IsChecking = true });
        await Task.WhenAll(Enum.GetValues<AgentBackend>().Select(b => CheckAsync(b, settings, cancellationToken)));
    }

    public async Task CheckAsync(AgentBackend backend, LoopConfig settings, CancellationToken cancellationToken = default)
    {
        Update(backend, s => s with { IsChecking = true });
        var (configured, detail, fingerprint) = await ProbeSetupAsync(backend, settings, cancellationToken);

        var remembered = Load().GetValueOrDefault(backend.ToString());
        var verified = configured && fingerprint is not null && remembered?.Fingerprint == fingerprint;
        lock (_gate)
        {
            if (fingerprint is null) _fingerprints.Remove(backend);
            else _fingerprints[backend] = fingerprint;
        }
        Update(backend, s => s with
        {
            IsConfigured = configured,
            SetupDetail = detail,
            IsVerified = verified,
            VerifiedAt = verified ? remembered!.VerifiedAt : null,
            VerifiedModel = verified ? remembered!.Model : null,
            LastError = configured ? s.LastError : null,
            IsChecking = false
        });
    }

    private async Task<(bool Configured, string Detail, string? Fingerprint)> ProbeSetupAsync(
        AgentBackend backend, LoopConfig settings, CancellationToken cancellationToken)
    {
        switch (backend)
        {
            case AgentBackend.Copilot:
            {
                var token = TokenResolver.Resolve(settings.TokenEnvironmentVariable);
                return token is null
                    ? (false, $"Environment variable {settings.TokenEnvironmentVariable} is not set.", null)
                    : (true, $"Token found in {settings.TokenEnvironmentVariable}.", Fingerprint("copilot", token));
            }
            case AgentBackend.ClaudeCode:
            {
                var token = ClaudeCli.ResolveToken(settings.ClaudeTokenEnvironmentVariable);
                string cli;
                try { cli = ClaudeCli.ResolvePath(settings.ClaudeCliPath); }
                catch (InvalidOperationException ex) { return (false, ex.Message, null); }
                return token is null
                    ? (false, $"Claude Code found ({cli}), but environment variable {settings.ClaudeTokenEnvironmentVariable} is not set.", null)
                    : (true, $"Token found in {settings.ClaudeTokenEnvironmentVariable}; CLI {cli}.", Fingerprint("claude", token, cli));
            }
            default:
            {
                string cli;
                try { cli = AntigravityCli.ResolvePath(settings.AntigravityCliPath); }
                catch (InvalidOperationException ex) { return (false, ex.Message, null); }
                await antigravityModels.RefreshAsync(settings.AntigravityCliPath, settings.IsolateAntigravity, force: true);
                if (antigravityModels.Models.Count == 0)
                    return (false, $"agy found ({cli}) but not signed in: {antigravityModels.Error ?? "no models listed"}", null);
                // agy keeps its sign-in to itself; the CLI path and the model list stand in for "same account".
                return (true, $"Signed in; {antigravityModels.Models.Count} models available. CLI {cli}.",
                    Fingerprint("antigravity", cli, string.Join(",", antigravityModels.Models.Select(m => m.Id))));
            }
        }
    }

    // ---------- verification (minimal prompt) ----------

    /// <summary>Runs a minimal prompt on the backend's cheapest model and remembers a success.</summary>
    public async Task VerifyAsync(AgentBackend backend, LoopConfig settings, CancellationToken cancellationToken = default)
    {
        await CheckAsync(backend, settings, cancellationToken);
        if (!Get(backend).IsConfigured) return;

        Update(backend, s => s with { IsVerifying = true, LastError = null });
        string? model = null;
        try
        {
            model = await PickCheapestModelAsync(backend, settings);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoopEngineering", "health-check");
            Directory.CreateDirectory(dir);

            var probe = settings.Clone();
            probe.WorkingDirectories = [dir];
            probe.UseAgentsMd = false;
            probe.WorkerBackend = backend;
            probe.EvaluatorBackend = backend; // the check runs as a read-only evaluator turn
            probe.EvaluatorModel = model ?? "";
            probe.EvaluatorReasoningEffort = "";
            probe.Model = model ?? "";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            await using var agent = _agentFactory(backend, probe);
            await agent.StartAsync(timeout.Token);
            var reply = await agent.RunTurnAsync(
                new AgentTurnRequest(RoundPhase.Evaluator, VerifyPrompt, TimeSpan.FromMinutes(3)), NullObserver.Instance, timeout.Token);

            if (!reply.Contains("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected reply: {Shorten(reply)}");

            string? fingerprint;
            lock (_gate) fingerprint = _fingerprints.GetValueOrDefault(backend);
            var now = DateTimeOffset.Now;
            if (fingerprint is not null) Remember(backend, new Verification(fingerprint, now, model));
            Update(backend, s => s with { IsVerified = true, VerifiedAt = now, VerifiedModel = model, IsVerifying = false });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Forget(backend);
            Update(backend, s => s with { IsVerified = false, VerifiedAt = null, LastError = ex.Message, IsVerifying = false });
        }
    }

    private async Task<string?> PickCheapestModelAsync(AgentBackend backend, LoopConfig settings)
    {
        switch (backend)
        {
            case AgentBackend.Copilot:
                await copilotModels.RefreshAsync(settings.TokenEnvironmentVariable);
                return copilotModels.Models
                    .Where(m => m.IsEnabled && !m.IsAuto && m.HasPrices)
                    .OrderBy(m => m.InputCreditsPerMillion + m.OutputCreditsPerMillion)
                    .FirstOrDefault()?.Id;
            case AgentBackend.ClaudeCode:
                return ClaudeCodeModels.All
                    .OrderBy(m => m.InputCreditsPerMillion + m.OutputCreditsPerMillion)
                    .First().Id;
            default:
                var models = antigravityModels.Models;
                return (models.FirstOrDefault(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase) && m.Id.EndsWith("-low"))
                        ?? models.FirstOrDefault(m => m.Id.EndsWith("-low"))
                        ?? models.FirstOrDefault())?.Id;
        }
    }

    private static ILoopAgent CreateAgent(AgentBackend backend, LoopConfig config) => backend switch
    {
        AgentBackend.ClaudeCode => new ClaudeCodeAgent(config),
        AgentBackend.Antigravity => new AntigravityAgent(config),
        _ => new CopilotLoopAgent(config)
    };

    // ---------- persistence ----------

    private sealed record Verification(string Fingerprint, DateTimeOffset VerifiedAt, string? Model);

    private Dictionary<string, Verification> Load()
    {
        try
        {
            if (File.Exists(storePath))
                return JsonSerializer.Deserialize<Dictionary<string, Verification>>(File.ReadAllText(storePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // start over
        }
        return [];
    }

    private void Remember(AgentBackend backend, Verification verification)
    {
        lock (_gate)
        {
            var all = Load();
            all[backend.ToString()] = verification;
            Save(all);
        }
    }

    private void Forget(AgentBackend backend)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(backend.ToString())) Save(all);
        }
    }

    private void Save(Dictionary<string, Verification> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            File.WriteAllText(storePath, JsonSerializer.Serialize(all, JsonOptions));
        }
        catch (IOException)
        {
            // verification still applies for this session
        }
    }

    /// <summary>Short one-way hash identifying a credential without storing it.</summary>
    internal static string Fingerprint(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts))))[..16];

    private void Update(AgentBackend backend, Func<BackendStatus, BackendStatus> change)
    {
        lock (_gate) _status[backend] = change(_status[backend]);
        try { Changed?.Invoke(); }
        catch { /* UI subscriber errors are not ours */ }
    }

    private static string Shorten(string text)
    {
        text = text.Trim().ReplaceLineEndings(" ");
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    private sealed class NullObserver : IAgentObserver
    {
        public static readonly NullObserver Instance = new();
        public void OnOutputDelta(string text) { }
        public void OnToolStarted(string toolName, string? detail) { }
        public void OnSubagentStarted(string id, string name, string? description) { }
        public void OnSubagentFinished(string id, bool succeeded, string? error) { }
        public void OnUsage(UsageSample usage) { }
        public void OnBilling(double premiumRequests, long userRequests, double? aiCredits) { }
        public void OnEstimatedCost(double usd) { }
        public void OnLog(string level, string message) { }
    }
}
