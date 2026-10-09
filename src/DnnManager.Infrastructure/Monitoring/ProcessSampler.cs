using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DnnManager.Infrastructure.Monitoring;

/// <param name="CpuPercent">Share of all processors since the previous sample of the same processes; null on the first one.</param>
/// <param name="MemoryPercent">Working set as a share of the PC's physical memory.</param>
/// <param name="ReadBytes">Bytes the processes read since they started - Windows' I/O counter: files, and for an IIS
/// worker its database connection (its HTTP traffic goes through http.sys, and is counted per site by IIS).</param>
/// <param name="Started">When the oldest of the processes started.</param>
public sealed record ProcessGroupStats(
    double? CpuPercent,
    long MemoryBytes,
    double MemoryPercent,
    long ReadBytes,
    long WriteBytes,
    DateTime? Started);

/// <summary>
/// CPU, memory and I/O of groups of processes - an IIS site's worker processes - measured between samples: each
/// call returns the CPU use since the previous call for those processes. Needs to run elevated to read w3wp.exe,
/// as DNN Manager does.
///
/// <para>It runs every two seconds for every site's workers, so each process is opened once and its handle kept
/// (limited query rights) until it ends or leaves the groups: a sample is then three calls a process - its times, its
/// memory, its I/O - instead of a <see cref="System.Diagnostics.Process"/> object and a new full-access handle each
/// time. A handle whose process has gone, or whose ID now belongs to another process (its start time differs), is
/// let go.</para>
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    private readonly Lock _lock = new();
    // The processes sampled, by ID: the open handle, when the process started, and its CPU time at the previous sample.
    private readonly Dictionary<int, Tracked> _tracked = [];

    private sealed class Tracked(SafeProcessHandle handle, DateTime started)
    {
        public SafeProcessHandle Handle { get; } = handle;
        public DateTime Started { get; } = started;
        public TimeSpan? Cpu { get; set; }
        public long At { get; set; }
    }

    /// <summary>
    /// Samples every group in <paramref name="groups"/> (key → process IDs) at once; a group whose processes are all gone
    /// is left out. Processes not in any group are forgotten.
    /// </summary>
    public IReadOnlyDictionary<TKey, ProcessGroupStats> Sample<TKey>(IReadOnlyDictionary<TKey, IReadOnlyList<int>> groups)
        where TKey : notnull
    {
        var totalMemory = NativeMethods.PhysicalMemory().Total;
        var result = new Dictionary<TKey, ProcessGroupStats>();
        lock (_lock)
        {
            var wanted = new HashSet<int>();
            // Each process read once a sample: sites that share an app pool share its worker - read again for the second
            // site, no time would have passed since the first read, and its CPU couldn't be told.
            var samples = new Dictionary<int, (double? CpuPercent, long Memory, long Read, long Write, DateTime Started)?>();
            foreach (var (key, pids) in groups)
            {
                wanted.UnionWith(pids);
                if (SampleGroup(pids, totalMemory, samples) is { } stats) result[key] = stats;
            }
            foreach (var gone in _tracked.Keys.Where(pid => !wanted.Contains(pid)).ToList()) Forget(gone);
        }
        return result;
    }

    private ProcessGroupStats? SampleGroup(IReadOnlyList<int> pids, ulong totalMemory,
        Dictionary<int, (double? CpuPercent, long Memory, long Read, long Write, DateTime Started)?> samples)
    {
        var found = false;
        var cpuKnown = true;
        double cpu = 0;
        long memory = 0, read = 0, write = 0;
        DateTime? started = null;

        foreach (var pid in pids)
        {
            if (!samples.TryGetValue(pid, out var once)) samples[pid] = once = Read(pid);
            if (once is not { } sample) continue;
            found = true;
            memory += sample.Memory;
            read += sample.Read;
            write += sample.Write;
            if (started is null || sample.Started < started) started = sample.Started;
            if (sample.CpuPercent is { } percent) cpu += percent;
            else cpuKnown = false;
        }

        if (!found) return null;
        return new ProcessGroupStats(cpuKnown ? Math.Clamp(cpu, 0, 100) : null, memory,
            totalMemory > 0 ? 100d * memory / totalMemory : 0, read, write, started);
    }

    private (double? CpuPercent, long Memory, long Read, long Write, DateTime Started)? Read(int pid)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!_tracked.TryGetValue(pid, out var tracked))
            {
                var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
                if (handle.IsInvalid)
                {
                    // Gone since the list was read, or not readable - leave it out.
                    handle.Dispose();
                    return null;
                }
                if (!NativeMethods.Times(handle, out var created, out _, out _))
                {
                    handle.Dispose();
                    return null;
                }
                _tracked[pid] = tracked = new Tracked(handle, created);
            }

            // Ended, or its ID is another process's now: opened again (a new process) or left out.
            if (!NativeMethods.Times(tracked.Handle, out var start, out var exited, out var cpuTime) || exited || start != tracked.Started)
            {
                Forget(pid);
                continue;
            }

            var now = Environment.TickCount64;
            double? percent = tracked.Cpu is { } before && now > tracked.At
                ? (cpuTime - before).TotalMilliseconds / (now - tracked.At) / Environment.ProcessorCount * 100
                : null;
            tracked.Cpu = cpuTime;
            tracked.At = now;
            long read = 0, write = 0;
            if (NativeMethods.GetProcessIoCounters(tracked.Handle, out var io))
            {
                read = (long)io.ReadTransferCount;
                write = (long)io.WriteTransferCount;
            }
            return (percent, NativeMethods.WorkingSet(tracked.Handle), read, write, start.ToLocalTime());
        }
        return null;
    }

    private void Forget(int pid)
    {
        if (_tracked.Remove(pid, out var tracked)) tracked.Handle.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var tracked in _tracked.Values) tracked.Handle.Dispose();
            _tracked.Clear();
        }
    }
}

/// <summary>
/// The IDs of this PC's IIS worker processes (w3wp.exe), cheaply enough to ask every two seconds: the IDs of all
/// processes (EnumProcesses - no snapshot of every process with its threads, as Process.GetProcessesByName takes), and
/// the program of an ID only the first time it is seen.
/// </summary>
public sealed class WorkerProcessList
{
    private readonly Lock _lock = new();
    // What each process ID seen last time was: true for a w3wp.exe.
    private Dictionary<int, bool> _known = [];
    private int[] _buffer = new int[1024];

    public HashSet<int> Read()
    {
        lock (_lock)
        {
            var ids = Ids();
            var known = new Dictionary<int, bool>(ids.Length);
            var workers = new HashSet<int>();
            foreach (var id in ids)
            {
                // An ID that comes back after a gap may be another process: only one seen at the previous read is reused.
                // One that can't be opened (yet) is asked again next time - a worker that is starting isn't missed.
                if (!_known.TryGetValue(id, out var isWorker))
                {
                    if (IsWorker(id) is not { } answer) continue;
                    isWorker = answer;
                }
                known[id] = isWorker;
                if (isWorker) workers.Add(id);
            }
            _known = known;
            return workers;
        }
    }

    private int[] Ids()
    {
        while (true)
        {
            if (!NativeMethods.EnumProcesses(_buffer, _buffer.Length * sizeof(int), out var bytes)) return [];
            var count = bytes / sizeof(int);
            // A full buffer may have been too small.
            if (count < _buffer.Length) return _buffer[..count];
            _buffer = new int[_buffer.Length * 2];
        }
    }

    // Null when it can't be told (the process can't be opened, or has no program name yet).
    private static bool? IsWorker(int id)
    {
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, id);
        if (handle.IsInvalid) return null;
        return NativeMethods.ImageName(handle) is { } name ? Path.GetFileName(name).Equals("w3wp.exe", StringComparison.OrdinalIgnoreCase) : null;
    }
}
