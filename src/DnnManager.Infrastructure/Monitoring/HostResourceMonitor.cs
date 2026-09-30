namespace DnnManager.Infrastructure.Monitoring;

/// <param name="CpuPercent">Use of all processors since the previous sample; null on the first one.</param>
/// <param name="DiskRoot">The drive the disk figures are about, e.g. <c>C:\</c>; null when it can't be read.</param>
public sealed record HostResources(
    ulong MemoryTotalBytes,
    ulong MemoryUsedBytes,
    double? CpuPercent,
    string? DiskRoot,
    long DiskTotalBytes,
    long DiskUsedBytes);

/// <summary>The disk part of <see cref="HostResources"/> - it changes slowly, so it is read less often.</summary>
public sealed record DiskUse(string? Root, long TotalBytes, long UsedBytes)
{
    public static readonly DiskUse Unknown = new(null, 0, 0);
}

/// <summary>
/// This PC's memory, processor and disk use, for the status bar. CPU use is measured between two samples, so
/// sample it on a timer; each call returns the use since the previous one.
/// </summary>
public sealed class HostResourceMonitor
{
    private readonly Lock _lock = new();
    private (long Idle, long Total)? _previous;

    /// <summary>Memory and CPU now, with <paramref name="disk"/> as last read by <see cref="SampleDisk"/>.</summary>
    public HostResources Sample(DiskUse disk)
    {
        var (total, available) = NativeMethods.PhysicalMemory();

        double? cpu = null;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var now = (Idle: idle, Total: kernel + user); // kernel time includes idle time
            lock (_lock)
            {
                if (_previous is { } before && now.Total > before.Total)
                    cpu = Math.Clamp(100d * (1 - (double)(now.Idle - before.Idle) / (now.Total - before.Total)), 0, 100);
                _previous = now;
            }
        }
        return new HostResources(total, total - available, cpu, disk.Root, disk.TotalBytes, disk.UsedBytes);
    }

    /// <param name="path">A path on the drive to report, e.g. the projects folder.</param>
    public static DiskUse SampleDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return DiskUse.Unknown;
            var drive = new DriveInfo(root);
            return drive.IsReady ? new DiskUse(root, drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace) : DiskUse.Unknown;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return DiskUse.Unknown;
        }
    }
}
