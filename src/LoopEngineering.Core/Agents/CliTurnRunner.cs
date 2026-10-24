using System.Diagnostics;
using System.Text;

namespace LoopEngineering.Core.Agents;

/// <summary>
/// Runs one headless agent CLI turn (Claude Code, Antigravity): writes the prompt to stdin, hands every
/// stdout line to a parser, enforces the turn timeout, and kills the whole process tree on stop or timeout.
/// </summary>
internal static class CliTurnRunner
{
    public sealed record Outcome(int ExitCode, string StderrTail);

    public static async Task<Outcome> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        string stdin,
        Action<string> onStdoutLine,
        TimeSpan timeout,
        string toolName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), // no BOM: CLIs parse the first byte as JSON / text
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

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
        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), linked.Token);
            process.StandardInput.Close();

            while (await process.StandardOutput.ReadLineAsync(linked.Token) is { } line)
                onStdoutLine(line);

            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{toolName} turn exceeded {timeout.TotalMinutes:0} minutes.");
        }

        string tail;
        lock (stderr) tail = string.Join(Environment.NewLine, stderr);
        return new Outcome(process.ExitCode, tail);
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
}
