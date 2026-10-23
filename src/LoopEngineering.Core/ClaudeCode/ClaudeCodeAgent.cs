using System.Diagnostics;
using System.Text;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.ClaudeCode;

/// <summary>
/// <see cref="ILoopAgent"/> backed by the Claude Code CLI in headless mode (`claude -p`). Each turn is a new
/// process with a fresh context, like the Copilot backend's new session per turn. Uses the Claude
/// subscription through a `claude setup-token` token from the configured variable.
/// </summary>
public sealed class ClaudeCodeAgent(LoopConfig config) : ILoopAgent
{
    /// <summary>Levels the CLI's --effort flag accepts.</summary>
    internal static readonly HashSet<string> SupportedEfforts = new(StringComparer.OrdinalIgnoreCase) { "low", "medium", "high", "max" };

    /// <summary>Tools taken away from roles that must not change files (evaluator, AGENTS.md writer).</summary>
    internal const string ReadOnlyDisallowedTools = "Edit,Write,MultiEdit,NotebookEdit";

    private string? _cliPath;
    private Dictionary<string, string>? _environment;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var token = ClaudeCli.ResolveToken(config.ClaudeTokenEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Environment variable '{config.ClaudeTokenEnvironmentVariable}' is not set. " +
                "Run `claude setup-token` and store the token in that variable.");

        _cliPath = ClaudeCli.ResolvePath(config.ClaudeCliPath);
        var configDir = ClaudeCli.IsolatedConfigDirectory;
        Directory.CreateDirectory(configDir);
        _environment = ClaudeCli.BuildEnvironment(config.ClaudeTokenEnvironmentVariable, token, configDir);
        return Task.CompletedTask;
    }

    internal static List<string> BuildArguments(LoopConfig config, AgentTurnRequest request)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--no-session-persistence",
            // Only what this app configures: no MCP servers or skills from anywhere else.
            "--strict-mcp-config",
            "--disable-slash-commands",
            // Unattended loop: approve tool use, as the Copilot backend does.
            "--dangerously-skip-permissions"
        };

        var model = config.ModelFor(request.Phase);
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model]);

        var effort = config.ReasoningEffortFor(request.Phase);
        if (!string.IsNullOrWhiteSpace(effort) && SupportedEfforts.Contains(effort)) args.AddRange(["--effort", effort]);

        // Rounds see every working folder; a guide turn works on one folder only.
        if (request.WorkingDirectory is null)
            foreach (var dir in config.WorkingDirectories.Skip(1)) args.AddRange(["--add-dir", dir]);

        // Enforce "do not modify files" with tool restrictions instead of trusting the prompt alone.
        if (request.Phase is RoundPhase.Evaluator or RoundPhase.Guide)
            args.AddRange(["--disallowed-tools", ReadOnlyDisallowedTools]);

        return args;
    }

    public async Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
    {
        if (_cliPath is null || _environment is null) throw new InvalidOperationException("Agent not started.");

        var effort = config.ReasoningEffortFor(request.Phase);
        if (!string.IsNullOrWhiteSpace(effort) && !SupportedEfforts.Contains(effort))
            observer.OnLog("warn", $"Claude Code does not support reasoning effort '{effort}'; using the model default.");

        var startInfo = new ProcessStartInfo(_cliPath)
        {
            WorkingDirectory = request.WorkingDirectory ?? config.PrimaryWorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in BuildArguments(config, request)) startInfo.ArgumentList.Add(arg);
        startInfo.Environment.Clear();
        foreach (var (key, value) in _environment) startInfo.Environment[key] = value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.Enqueue(e.Data);
                if (stderr.Count > 40) stderr.Dequeue();
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        var parser = new ClaudeStreamParser(observer);
        try
        {
            // The prompt goes through stdin: no command-line length limit, no quoting issues.
            await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                parser.ProcessLine(line);

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Claude Code turn exceeded {request.Timeout.TotalMinutes:0} minutes.");
        }

        if (!parser.HasResult)
        {
            string tail;
            lock (stderr) tail = string.Join(Environment.NewLine, stderr);
            throw new InvalidOperationException(
                $"Claude Code exited with code {process.ExitCode} without a result." + (tail.Length > 0 ? $" {tail}" : ""));
        }
        if (parser.IsError)
            throw new InvalidOperationException($"Claude Code reported an error: {parser.ResultText}");

        return parser.ResultText;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* access denied while exiting */ }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
