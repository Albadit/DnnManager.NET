using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// Windows' power throttling of this process - EcoQoS, the half of Task Manager's "Efficiency mode" that is safe for
/// an app the user comes back to. While it is on, Windows runs the process at its most power-efficient processor speed
/// (and on efficient cores where the processor has them), on AC power too, and ignores its requests for a finer timer
/// resolution. It makes CPU-bound work slower, so it is only for while nobody waits for the app.
/// <para>
/// The other half of Task Manager's switch, a lower priority class, is left alone on purpose: the shells, tools and
/// IDEs DNN Manager starts would inherit it for their whole life. So are background mode (lower I/O and memory
/// priority - not for a process that interacts with the user), the working set and the garbage collector.
/// </para>
/// </summary>
public static class PowerThrottling
{
    // ProcessPowerThrottling in PROCESS_INFORMATION_CLASS, and the PROCESS_POWER_THROTTLING_* values (processthreadsapi.h).
    private const int ProcessPowerThrottling = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;
    private const int ErrorInvalidParameter = 87;

    /// <summary>PROCESS_POWER_THROTTLING_STATE: which settings this process makes (ControlMask), and to what (StateMask).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ThrottlingState information, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Asks Windows to run this process power-efficiently (EcoQoS) and to ignore its timer-resolution requests. False
    /// when Windows refused - one older than Windows 10 version 1709 has no such setting.
    /// </summary>
    public static bool TryEnterEcoQoS()
    {
        // Both in one call: each call replaces the whole setting, so a second one would undo the first. A Windows older
        // than 11 doesn't know the timer-resolution flag and refuses the call (ERROR_INVALID_PARAMETER) - then without it.
        if (Set(ExecutionSpeed | IgnoreTimerResolution)) return true;
        return Marshal.GetLastPInvokeError() == ErrorInvalidParameter && Set(ExecutionSpeed);
    }

    /// <summary>
    /// Leaves it to Windows again, as for any other process: high QoS while the window is in front, lower while it is
    /// minimized, as Windows sees fit. (Not "never throttle" - that would be a setting of its own.) False when refused.
    /// </summary>
    public static bool Reset() => Set(0);

    /// <summary>The Windows error code of the last call that was refused (on this thread), for a log line.</summary>
    public static int LastError => Marshal.GetLastPInvokeError();

    private static bool Set(uint flags)
    {
        var state = new ThrottlingState { Version = CurrentVersion, ControlMask = flags, StateMask = flags };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ThrottlingState>());
    }
}
