using System.Diagnostics;

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
/// </summary>
public sealed class ProcessSampler
{
    private readonly Lock _lock = new();
    // CPU time of each process at its previous sample.
    private Dictionary<int, (TimeSpan Cpu, DateTime At)> _previous = new();
    private Dictionary<int, (TimeSpan Cpu, DateTime At)> _current = new();

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
            _current = new Dictionary<int, (TimeSpan, DateTime)>();
            foreach (var (key, pids) in groups)
            {
                if (SampleGroup(pids, totalMemory) is { } stats) result[key] = stats;
            }
            _previous = _current;
        }
        return result;
    }

    private ProcessGroupStats? SampleGroup(IReadOnlyList<int> pids, ulong totalMemory)
    {
        var found = false;
        var cpuKnown = true;
        double cpu = 0;
        long memory = 0, read = 0, write = 0;
        DateTime? started = null;

        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var now = DateTime.UtcNow;
                var cpuTime = process.TotalProcessorTime;
                // From the handle, as the CPU time, start time and I/O are: no snapshot of every process for it.
                memory += NativeMethods.WorkingSet(process.Handle);
                var start = process.StartTime;
                if (started is null || start < started) started = start;
                if (NativeMethods.GetProcessIoCounters(process.Handle, out var io))
                {
                    read += (long)io.ReadTransferCount;
                    write += (long)io.WriteTransferCount;
                }

                _current[pid] = (cpuTime, now);
                if (_previous.TryGetValue(pid, out var before) && now > before.At)
                    cpu += (cpuTime - before.Cpu).TotalMilliseconds / (now - before.At).TotalMilliseconds / Environment.ProcessorCount * 100;
                else
                    cpuKnown = false;
                found = true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone since the list was read, or not readable - leave it out.
            }
        }

        if (!found) return null;
        return new ProcessGroupStats(cpuKnown ? Math.Clamp(cpu, 0, 100) : null, memory,
            totalMemory > 0 ? 100d * memory / totalMemory : 0, read, write, started);
    }
}
