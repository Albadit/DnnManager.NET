using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// Task Manager's "Efficiency mode" for this process: EcoQoS and the Idle priority class - both, as Task Manager sets
/// them, so it shows the process with its leaf. With EcoQoS Windows runs the process at its most power-efficient
/// processor speed (and on efficient cores where the processor has them), on AC power too, and ignores its requests
/// for a finer timer resolution; with Idle priority it only gets the processor when nothing else wants it. Both make
/// CPU-bound work slower, so they are only for while nobody waits for the app.
/// <para>
/// A program started while the priority is Idle inherits it. The terminal starts its shells at Normal priority
/// whatever this process has; everything else DNN Manager opens (an IDE, SSMS, the browser) is opened from the window,
/// which ends efficiency mode first. Background mode (lower I/O and memory priority - not for a process that interacts
/// with the user), the working set and the garbage collector are left alone.
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

    // The priority class before efficiency mode lowered it, to go back to.
    private static ProcessPriorityClass? _priorityBefore;
    private static volatile bool _active;

    /// <summary>
    /// The process is in efficiency mode now - its threads may wait seconds for a processor while others are busy, so a
    /// time measured meanwhile can be longer than what it measures.
    /// </summary>
    public static bool IsActive => _active;

    /// <summary>
    /// Enters efficiency mode: asks Windows to run this process power-efficiently (EcoQoS) and to ignore its
    /// timer-resolution requests, and lowers its priority class to Idle. False when Windows refused EcoQoS - one older
    /// than Windows 10 version 1709 has no such setting; the priority is left alone then.
    /// </summary>
    public static bool TryEnter()
    {
        // Both in one call: each call replaces the whole setting, so a second one would undo the first. A Windows older
        // than 11 doesn't know the timer-resolution flag and refuses the call (ERROR_INVALID_PARAMETER) - then without it.
        var eco = Set(ExecutionSpeed | IgnoreTimerResolution) ||
                  (Marshal.GetLastPInvokeError() == ErrorInvalidParameter && Set(ExecutionSpeed));
        if (eco) SetIdlePriority();
        _active = eco;
        return eco;
    }

    /// <summary>
    /// Leaves efficiency mode: the priority class it had before, and the speed left to Windows again, as for any other
    /// process - high QoS while the window is in front, lower while it is minimized, as Windows sees fit. (Not "never
    /// throttle" - that would be a setting of its own.) False when Windows refused.
    /// </summary>
    public static bool Reset()
    {
        RestorePriority();
        _active = false;
        return Set(0);
    }

    /// <summary>The Windows error code of the last call that was refused (on this thread), for a log line.</summary>
    public static int LastError => Marshal.GetLastPInvokeError();

    private static void SetIdlePriority()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            if (self.PriorityClass == ProcessPriorityClass.Idle) return;
            _priorityBefore = self.PriorityClass;
            self.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // EcoQoS alone still saves most of it.
        }
    }

    private static void RestorePriority()
    {
        if (_priorityBefore is not { } before) return;
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = before;
            _priorityBefore = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Tried again with the next Reset.
        }
    }

    private static bool Set(uint flags)
    {
        var state = new ThrottlingState { Version = CurrentVersion, ControlMask = flags, StateMask = flags };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<ThrottlingState>());
    }
}
