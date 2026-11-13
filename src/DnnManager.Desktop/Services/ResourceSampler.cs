using DnnManager.Infrastructure.Monitoring;

namespace DnnManager.Desktop.Services;

/// <summary>
/// This computer's memory, processor and disk use for the status bar - what the Windows app's ServerStore samples every
/// two seconds (the disk every ten). On Windows <see cref="HostResourceMonitor"/>; on Linux /proc; on macOS the memory
/// .NET knows of and the disk (no processor figure). Off the UI thread; paused while nobody sees it (minimized).
/// </summary>
public sealed class ResourceSampler : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private readonly HostResourceMonitor? _windows = OperatingSystem.IsWindows() ? new HostResourceMonitor() : null;
    private readonly Func<string> _diskPath;
    private readonly Timer _timer;
    private DiskUse _disk = DiskUse.Unknown;
    private int _ticks;
    private (long Idle, long Total)? _linuxBefore;
    private int _sampling;

    public ResourceSampler(Func<string> diskPath)
    {
        _diskPath = diskPath;
        _timer = new Timer(_ => Sample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>A new sample - on a thread-pool thread.</summary>
    public event Action<HostResources>? Sampled;

    public HostResources? Last { get; private set; }

    public void Start()
    {
        _windows?.Restart();
        _linuxBefore = null;
        _ticks = 0;
        _timer.Change(TimeSpan.Zero, Every);
    }

    public void Pause() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            if (_ticks++ % 5 == 0) _disk = HostResourceMonitor.SampleDisk(_diskPath());
            var sample = _windows?.Sample(_disk) ?? (OperatingSystem.IsLinux() ? Linux() : Other());
            Last = sample;
            Sampled?.Invoke(sample);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to show this time.
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private HostResources Linux()
    {
        ulong total = 0, available = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kilobytes(line);
        }
        double? cpu = null;
        var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        var now = (Idle: fields[3] + (fields.Length > 4 ? fields[4] : 0), Total: fields.Sum());
        if (_linuxBefore is { } before && now.Total > before.Total)
            cpu = Math.Clamp(100d * (1 - (double)(now.Idle - before.Idle) / (now.Total - before.Total)), 0, 100);
        _linuxBefore = now;
        return new HostResources(total, total - Math.Min(total, available), cpu, _disk.Root, _disk.TotalBytes, _disk.UsedBytes);

        static ulong Kilobytes(string line) =>
            ulong.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
    }

    private HostResources Other()
    {
        var memory = GC.GetGCMemoryInfo();
        var total = (ulong)Math.Max(0, memory.TotalAvailableMemoryBytes);
        var used = (ulong)Math.Max(0, memory.MemoryLoadBytes);
        // The processor's use isn't known here: the status bar leaves it out.
        return new HostResources(total, Math.Min(used, total), null, _disk.Root, _disk.TotalBytes, _disk.UsedBytes);
    }

    public void Dispose() => _timer.Dispose();
}
