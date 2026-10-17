using System.Collections;
using System.Text.RegularExpressions;
using GitHub.Copilot;

namespace LoopEngineering.Core.Copilot;

/// <summary>
/// Creates <see cref="CopilotClient"/>s that authenticate only with the token from the configured
/// environment variable. The SDK is not allowed to look for credentials itself.
/// </summary>
public static partial class CopilotClients
{
    /// <summary>
    /// Variables the Copilot runtime would otherwise search for a token. They are removed from the
    /// runtime's environment so the only credential it can see is the one we pass explicitly.
    /// </summary>
    internal static readonly string[] AmbientTokenVariables =
    [
        "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN",
        "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "COPILOT_TOKEN"
    ];

    public static async Task<CopilotClient> StartAsync(string tokenVariable, string? workingDirectory, CancellationToken cancellationToken)
    {
        var token = TokenResolver.Resolve(tokenVariable)
            ?? throw new InvalidOperationException(
                $"Environment variable '{tokenVariable}' is not set. Set it to a GitHub token with Copilot access.");

        var client = new CopilotClient(new CopilotClientOptions
        {
            GitHubToken = token,
            UseLoggedInUser = false,
            Environment = BuildRuntimeEnvironment(tokenVariable),
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory
        });
        try
        {
            await client.StartAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Lists models, which needs the same permission as chat requests but costs nothing.
    /// A token without the "Copilot Requests" permission still authenticates (and can read quota),
    /// so this is the earliest point where that mistake shows up.
    /// </summary>
    public static async Task VerifyModelAccessAsync(CopilotClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.ListModelsAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Copilot rejected the token: {ExtractReason(ex.Message)}", ex);
        }
    }

    /// <summary>Pulls the HTTP body text out of the runtime's JSON-ish error message, if present.</summary>
    internal static string ExtractReason(string message)
    {
        var match = BodyPattern().Match(message);
        if (!match.Success) return message;
        var body = Regex.Unescape(match.Groups[1].Value).Trim();
        return body.Replace("checking third-party user token: ", "", StringComparison.Ordinal);
    }

    [GeneratedRegex("\"body\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex BodyPattern();

    public static async Task StopQuietlyAsync(CopilotClient client)
    {
        try { await client.StopAsync(); }
        catch { await client.ForceStopAsync(); }
        await client.DisposeAsync();
    }

    /// <summary>Copy of the current environment minus every variable a token could be discovered from.</summary>
    internal static Dictionary<string, string> BuildRuntimeEnvironment(string tokenVariable)
    {
        var excluded = new HashSet<string>(AmbientTokenVariables, StringComparer.OrdinalIgnoreCase) { tokenVariable };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!excluded.Contains(key) && entry.Value is string value)
                env[key] = value;
        }
        return env;
    }
}
