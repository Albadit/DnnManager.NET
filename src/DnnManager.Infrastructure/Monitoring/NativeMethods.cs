using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Monitoring;

/// <summary>The Win32 calls behind the resource figures - .NET has no managed API for machine-wide CPU and memory.</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Idle, kernel (which includes idle) and user time of all processors, in 100 ns ticks.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    /// <summary>
    /// A process's working set, read from its handle. <see cref="System.Diagnostics.Process.WorkingSet64"/> takes a
    /// snapshot of every process on the PC for it - once per process, every couple of seconds.
    /// </summary>
    public static long WorkingSet(IntPtr process)
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return GetProcessMemoryInfo(process, ref counters, counters.Size) ? (long)(ulong)counters.WorkingSetSize : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    public const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>A process's start (UTC), whether it has ended, and its CPU time (kernel and user) - in one call.</summary>
    public static bool Times(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out DateTime started, out bool exited, out TimeSpan cpu)
    {
        if (!GetProcessTimes(process, out var creation, out var exit, out var kernel, out var user))
        {
            (started, exited, cpu) = (default, false, default);
            return false;
        }
        (started, exited, cpu) = (DateTime.FromFileTimeUtc(creation), exit != 0, TimeSpan.FromTicks(kernel + user));
        return true;
    }

    /// <summary>The IDs of all processes - no more than that (a snapshot with their threads is what Process.GetProcesses takes).</summary>
    [DllImport("psapi.dll", EntryPoint = "EnumProcesses", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumProcesses([Out] int[] ids, int size, out int bytesReturned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags,
        [Out] char[] name, ref int size);

    /// <summary>The program a process runs (its full path); null when Windows won't say.</summary>
    public static string? ImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out IoCounters counters);

    /// <summary>A process's working set, read from its handle.</summary>
    public static long WorkingSet(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return GetProcessMemoryInfo(process, ref counters, counters.Size) ? (long)(ulong)counters.WorkingSetSize : 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(Microsoft.Win32.SafeHandles.SafeProcessHandle process, ref ProcessMemoryCounters counters, uint size);

    /// <summary>Physical memory: (total, available) bytes; (0, 0) when it can't be read.</summary>
    public static (ulong Total, ulong Available) PhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (status.TotalPhys, status.AvailPhys) : (0, 0);
    }
}
