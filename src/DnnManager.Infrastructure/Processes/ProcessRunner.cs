using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DnnManager.Infrastructure.Processes;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;
}

public sealed class ProcessRunner
{
    /// <param name="onOutput">Called with each stdout / stderr line as it arrives, e.g. to show progress.</param>
    /// <param name="stdin">Written to the process's standard input, which is then closed (e.g. <c>docker compose -f -</c>).</param>
    /// <param name="timeout">
    /// How long it may run - then it is ended, with its child processes, and the run fails (exit code -1). None: until it
    /// ends or is cancelled. For a quick question to a program that can hang (docker while Docker Desktop is half started).
    /// </param>
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default,
        IDictionary<string, string?>? env = null, Action<string>? onOutput = null, string? stdin = null, TimeSpan? timeout = null)
    {
        // Run as Administrator: only a copy of the program that nobody else can change.
        if (TrustedPrograms.Find(fileName, out var refused) is not { } program)
            return new ProcessResult
            {
                ExitCode = -1,
                StdErr = refused is null
                    ? $"Could not start '{fileName}': it isn't installed (or not on PATH)."
                    : $"Didn't start {refused}: programs without administrator rights could change it, and DNN Manager runs it as " +
                      "Administrator. Install it for all users (in Program Files) - DNN Manager uses that copy."
            };
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            // The executable isn't installed / on PATH (e.g. no Docker or winget). Report it as an
            // ordinary failed run (exit code -1) so callers can show a "tool missing" message instead
            // of the exception unwinding the whole operation.
            return new ProcessResult { ExitCode = -1, StdErr = $"Could not start '{fileName}': {ex.Message}" };
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } after) limit.CancelAfter(after);
        try
        {
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin.AsMemory(), limit.Token);
                p.StandardInput.Close();
            }
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Its time is up: ended like a cancelled one, and said as a failed run.
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await p.WaitForExitAsync(grace.Token);
            }
            catch { /* already gone, or not ours to kill */ }
            return new ProcessResult { ExitCode = -1, StdOut = stdout.ToString(), StdErr = $"'{fileName}' didn't finish within {timeout!.Value.TotalSeconds:0} seconds." };
        }
        catch (OperationCanceledException)
        {
            // Cancellation only abandons the *wait* - the child keeps running. These are docker,
            // sqlcmd and powershell invocations that hold container locks, SQL connections and file
            // handles, so take the whole tree down (docker CLI spawns helpers) and wait for it to
            // actually exit before letting the caller move on.
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                // Bounded: reaping a killed child must not turn a cancellation into a hang.
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await p.WaitForExitAsync(grace.Token);
            }
            catch { /* already gone, or not ours to kill */ }
            throw;
        }
        return new ProcessResult { ExitCode = p.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
    }
}
