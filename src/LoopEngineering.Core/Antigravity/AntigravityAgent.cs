using System.Text.Json;
using LoopEngineering.Core.Agents;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Antigravity;

/// <summary>
/// <see cref="ILoopAgent"/> backed by the Google Antigravity CLI (`agy`) in headless mode. Each turn is a new
/// process with a fresh context. agy has no token option: it uses its own saved Google sign-in (Google AI
/// Pro/Ultra), so the user signs in once by running `agy` interactively.
/// </summary>
public sealed class AntigravityAgent(LoopConfig config) : ILoopAgent
{
    private string? _cliPath;
    private Dictionary<string, string>? _environment;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cliPath = AntigravityCli.ResolvePath(config.AntigravityCliPath);
        var home = AntigravityCli.IsolatedHomeDirectory;
        if (config.IsolateAntigravity) Directory.CreateDirectory(home);
        _environment = AntigravityCli.BuildEnvironment(config.IsolateAntigravity, home);

        // Listing models costs nothing and fails fast when there is no saved sign-in.
        var models = await AntigravityCli.ListModelsAsync(_cliPath, config.IsolateAntigravity, cancellationToken);
        foreach (var phase in new[] { RoundPhase.Worker, RoundPhase.Evaluator, RoundPhase.Guide })
        {
            if (config.BackendFor(phase) != AgentBackend.Antigravity) continue;
            var model = config.ModelFor(phase);
            if (!string.IsNullOrWhiteSpace(model) && models.All(m => m.Id != model))
                throw new InvalidOperationException($"Antigravity model '{model}' is not available to your account (see `agy models`).");
        }
    }

    internal static List<string> BuildArguments(LoopConfig config, AgentTurnRequest request)
    {
        var args = new List<string>
        {
            // The prompt is sent as one stream-json user event on stdin: no command-line length limit.
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--disable-slash-commands",
            // Unattended loop: approve tool use, as the other backends do.
            "--dangerously-skip-permissions"
        };

        // agy encodes the reasoning effort in the model variant (e.g. gemini-4-argon-high).
        var model = config.ModelFor(request.Phase);
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model]);

        if (request.WorkingDirectory is null)
            foreach (var dir in config.WorkingDirectories.Skip(1)) args.AddRange(["--add-dir", dir]);

        return args;
    }

    /// <summary>The stream-json input line agy expects for one user turn.</summary>
    internal static string BuildInput(string prompt) =>
        JsonSerializer.Serialize(new { @event = "user", message = new { role = "user", content = prompt } }) + "\n";

    public async Task<string> RunTurnAsync(AgentTurnRequest request, IAgentObserver observer, CancellationToken cancellationToken)
    {
        if (_cliPath is null || _environment is null) throw new InvalidOperationException("Agent not started.");

        var parser = new AntigravityStreamParser(observer);
        var outcome = await CliTurnRunner.RunAsync(
            _cliPath, BuildArguments(config, request), _environment,
            request.WorkingDirectory ?? config.PrimaryWorkingDirectory,
            BuildInput(request.Prompt), parser.ProcessLine, request.Timeout, "Antigravity", cancellationToken);

        if (!parser.HasResult)
            throw new InvalidOperationException(
                $"Antigravity exited with code {outcome.ExitCode} without a result." + (outcome.StderrTail.Length > 0 ? $" {outcome.StderrTail}" : ""));
        if (parser.IsError)
            throw new InvalidOperationException($"Antigravity reported an error: {parser.Error ?? parser.ResultText}");

        return parser.ResultText;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
