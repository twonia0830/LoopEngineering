using System.Collections;
using System.Diagnostics;
using System.Text;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Antigravity;

/// <summary>Locates the Antigravity CLI (`agy`), prepares its environment and lists the models the account can use.</summary>
public static class AntigravityCli
{
    /// <summary>
    /// App-owned home folder used when isolation is on. agy keeps its settings, plugins, MCP servers and
    /// GEMINI.md under the home folder; the Google sign-in lives in the OS credential store and keeps working.
    /// </summary>
    public static string IsolatedHomeDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoopEngineering", "antigravity-home");

    public static string ResolvePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)) return configuredPath;
            throw new InvalidOperationException($"Antigravity CLI not found at {configuredPath}.");
        }

        var names = OperatingSystem.IsWindows() ? new[] { "agy.exe", "agy.cmd" } : new[] { "agy" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin"));
        foreach (var dir in dirs)
        foreach (var name in names)
        {
            var candidate = Path.Combine(dir.Trim(), name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            "Antigravity CLI (agy) not found on PATH. Install it and sign in once by running `agy`, or set the CLI path in the setup form.");
    }

    /// <summary>
    /// Current environment for agy. With isolation, HOME/USERPROFILE point at an app-owned folder, while the
    /// locations build tools and git need (NuGet cache, dotnet home, global git config) still point at the real
    /// profile so commands the agent runs behave normally. A GEMINI_API_KEY is removed so the run uses the
    /// subscription sign-in rather than API billing.
    /// </summary>
    internal static Dictionary<string, string> BuildEnvironment(bool isolate, string isolatedHome)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (entry.Value is not string value) continue;
            if (key.Equals("GEMINI_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GOOGLE_API_KEY", StringComparison.OrdinalIgnoreCase))
                continue;
            env[key] = value;
        }

        if (!isolate) return env;

        var realProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        env.TryAdd("NUGET_PACKAGES", Path.Combine(realProfile, ".nuget", "packages"));
        env.TryAdd("DOTNET_CLI_HOME", realProfile);
        var gitConfig = Path.Combine(realProfile, ".gitconfig");
        if (File.Exists(gitConfig)) env.TryAdd("GIT_CONFIG_GLOBAL", gitConfig);

        env["USERPROFILE"] = isolatedHome;
        env["HOME"] = isolatedHome;
        return env;
    }

    /// <summary>Runs `agy models` (free, no model call) and parses "slug&lt;TAB&gt;Display name" lines.</summary>
    public static async Task<IReadOnlyList<ModelOption>> ListModelsAsync(string cliPath, bool isolate, CancellationToken cancellationToken)
    {
        var home = IsolatedHomeDirectory;
        if (isolate) Directory.CreateDirectory(home);

        var startInfo = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("models");
        startInfo.Environment.Clear();
        foreach (var (key, value) in BuildEnvironment(isolate, home)) startInfo.Environment[key] = value;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start agy.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var models = ParseModels(await stdout);
        if (models.Count == 0)
        {
            var error = (await stderr).Trim();
            throw new InvalidOperationException(
                $"agy listed no models (exit {process.ExitCode}). {(error.Length > 0 ? error : "Sign in once by running `agy` in a terminal.")}");
        }
        return models;
    }

    internal static IReadOnlyList<ModelOption> ParseModels(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && !parts[0].Contains(' '))
            // agy encodes the effort in the model variant (e.g. gemini-4-argon-high), so there is no separate effort list.
            .Select(parts => new ModelOption(parts[0], parts[1].Trim(), null, [], null, null, null, null))
            .ToList();
}
