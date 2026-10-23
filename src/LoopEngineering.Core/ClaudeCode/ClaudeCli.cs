using System.Collections;
using LoopEngineering.Core.Copilot;

namespace LoopEngineering.Core.ClaudeCode;

/// <summary>Locates the Claude Code CLI and prepares an isolated, token-only environment for it.</summary>
public static class ClaudeCli
{
    /// <summary>The variable Claude Code reads a `claude setup-token` token from.</summary>
    public const string OAuthTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>
    /// Credentials Claude Code would otherwise pick up on its own. They are removed so the only credential
    /// the process can see is the token from the configured variable.
    /// </summary>
    internal static readonly string[] AmbientCredentialVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", OAuthTokenVariable,
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"
    ];

    /// <summary>
    /// Config folder owned by this app. Pointing CLAUDE_CONFIG_DIR here keeps the user's own Claude Code
    /// setup (global CLAUDE.md, plugins, hooks, MCP servers, saved login) out of the loop.
    /// </summary>
    public static string IsolatedConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoopEngineering", "claude-config");

    /// <summary>Returns the CLI path or throws with a hint on how to install / configure it.</summary>
    public static string ResolvePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)) return configuredPath;
            throw new InvalidOperationException($"Claude Code CLI not found at {configuredPath}.");
        }

        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var dir in dirs)
        foreach (var name in names)
        {
            var candidate = Path.Combine(dir.Trim(), name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            "Claude Code CLI (claude) not found on PATH. Install Claude Code or set the CLI path in the setup form.");
    }

    /// <summary>
    /// Copy of the current environment without ambient Claude credentials or markers of an enclosing Claude
    /// Code session, plus the explicit token and the isolated config folder.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(string tokenVariable, string token, string configDirectory)
    {
        var excluded = new HashSet<string>(AmbientCredentialVariables, StringComparer.OrdinalIgnoreCase) { tokenVariable };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (excluded.Contains(key) || entry.Value is not string value) continue;
            // Markers of a parent Claude Code session would make the child think it is nested.
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase))
                continue;
            env[key] = value;
        }

        env[OAuthTokenVariable] = token;
        env["CLAUDE_CONFIG_DIR"] = configDirectory;
        return env;
    }

    public static string? ResolveToken(string tokenVariable) => TokenResolver.Resolve(tokenVariable);
}
