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
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct = default,
        IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (Win32Exception ex)
        {
            // The executable isn't installed / on PATH (e.g. no Docker). Report it as an ordinary
            // failed run so callers take their "tool missing" path instead of unwinding the whole
            // operation - setup is meant to skip the database step when Docker is absent, not abort.
            return new ProcessResult { ExitCode = -1, StdErr = $"Could not start '{fileName}': {ex.Message}" };
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
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
