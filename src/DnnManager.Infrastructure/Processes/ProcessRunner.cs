using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        ChildEnvironment.Apply(psi.Environment);
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
        // Ended with DNN Manager, however it ends (a crash, Windows signing out): never left running on its own.
        ChildJob.Add(p);
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

/// <summary>
/// A Windows job object holding the programs <see cref="ProcessRunner"/> starts, closed by Windows when DNN Manager's
/// process ends - which ends them too (KILL_ON_JOB_CLOSE). Without it a docker, sqlcmd or SqlPackage run would go on after
/// DNN Manager crashed or was ended, holding files and databases nobody waits for. Only the program itself: what it
/// starts in turn (a LocalDB instance sqllocaldb starts, which sites use) leaves the job silently and is kept.
/// </summary>
internal static class ChildJob
{
    private static readonly Lazy<SafeFileHandle?> Job = new(Create);

    /// <summary>Puts <paramref name="process"/> in the job - best effort: a process that can't be added runs as before.</summary>
    public static void Add(Process process)
    {
        try
        {
            if (Job.Value is { IsInvalid: false } job) AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already ended */ }
    }

    /// <summary>Whether the job could be made - false only where Windows refuses one.</summary>
    internal static bool IsAvailable => Job.Value is { IsInvalid: false };

    private static SafeFileHandle? Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = KillOnJobClose | SilentBreakawayOk }
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size)) return job;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        job.Dispose();
        return null;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint SilentBreakawayOk = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
