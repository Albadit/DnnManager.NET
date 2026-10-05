using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Whether a window can be seen at all while it isn't the one in front: covered completely by other windows, on
/// another virtual desktop, or behind a locked screen - as Chromium's NativeWindowOcclusionTracker decides it (what
/// Docker Desktop, Edge and other Chromium apps go by). While the window is active it is in front and seen, so the
/// tracker only listens while it isn't: Windows' window events (foreground, show / hide, move, minimize, cloak) start a
/// look 100 ms later, when a burst of them has settled.
/// </summary>
internal sealed class WindowOcclusion : IDisposable
{
    // How long after the last window event to look again - Chromium waits 16 to 100 ms.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    private readonly Window _window;
    private readonly DispatcherTimer _look;
    private readonly List<IntPtr> _hooks = [];
    // Kept alive while the hooks are set - Windows calls it.
    private readonly WinEventProc _onEvent;
    private bool _locked;

    public WindowOcclusion(Window window)
    {
        _window = window;
        _onEvent = OnWinEvent;
        _look = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = Settle };
        _look.Tick += (_, _) =>
        {
            _look.Stop();
            Look();
        };
        // Listening only while it is shown but not in front: active it is seen, minimized or hidden it isn't there at all.
        window.Activated += (_, _) => Follow(false);
        window.Deactivated += (_, _) => Follow(window.IsVisible && window.WindowState != WindowState.Minimized);
        window.StateChanged += (_, _) => Follow(window.IsVisible && !window.IsActive && window.WindowState != WindowState.Minimized);
        window.IsVisibleChanged += (_, _) => Follow(window.IsVisible && !window.IsActive && window.WindowState != WindowState.Minimized);
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>No part of the window can be seen (minimized aside - that is the window's own state).</summary>
    public bool IsOccluded { get; private set; }

    /// <summary><see cref="IsOccluded"/> changed. Raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Listens to the other windows while this one isn't in front; in front it is seen.</summary>
    private void Follow(bool listen)
    {
        Unhook();
        _look.Stop();
        if (listen)
        {
            // Out of context: Windows posts the events to this (the UI) thread's message queue - no DLL is injected.
            foreach (var (min, max) in Events)
            {
                var hook = SetWinEventHook(min, max, IntPtr.Zero, _onEvent, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
                if (hook != IntPtr.Zero) _hooks.Add(hook);
            }
            _look.Start();
        }
        else
        {
            Set(_locked);
        }
    }

    private void OnWinEvent(IntPtr hook, uint winEvent, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only windows themselves - not the cursor or a caret moving, which report location changes all the time.
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == IntPtr.Zero) return;
        _look.Stop();
        _look.Start();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _locked = e.Reason == SessionSwitchReason.SessionLock;
            // Unlocked: the windows are where they were, but look again.
            if (_locked) Set(true);
            else if (_window.IsActive) Set(false);
            else Look();
        });
    }

    private void Look()
    {
        if (_window.IsActive) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        Set(_locked || (hwnd != IntPtr.Zero && Covered(hwnd)));
    }

    private void Set(bool occluded)
    {
        if (occluded == IsOccluded) return;
        IsOccluded = occluded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ─── Is any part of it to be seen ─────────────────────────────────────

    /// <summary>
    /// True when the windows above <paramref name="hwnd"/> together cover all of it that is on the screen, or it is on
    /// another virtual desktop (cloaked). A minimized or hidden window isn't covered - it isn't there at all.
    /// </summary>
    private static bool Covered(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (IsCloaked(hwnd)) return true;
        if (!FrameBounds(hwnd, out var bounds)) return false;

        // What of it is on the screen; parts off every screen can't be seen anyway.
        var screen = new Rect32
        {
            Left = GetSystemMetrics(SmXVirtualScreen),
            Top = GetSystemMetrics(SmYVirtualScreen),
        };
        screen.Right = screen.Left + GetSystemMetrics(SmCxVirtualScreen);
        screen.Bottom = screen.Top + GetSystemMetrics(SmCyVirtualScreen);
        var visible = CreateRectRgn(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var clip = CreateRectRgn(screen.Left, screen.Top, screen.Right, screen.Bottom);
        var other = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (CombineRgn(visible, visible, clip, RgnAnd) == NullRegion) return true;
            // The windows above it, from the nearest to the top of the Z-order.
            for (var above = GetWindow(hwnd, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
            {
                if (!Covers(above, out var rect)) continue;
                SetRectRgn(other, rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (CombineRgn(visible, visible, other, RgnDiff) == NullRegion) return true;
            }
            return false;
        }
        finally
        {
            DeleteObject(visible);
            DeleteObject(clip);
            DeleteObject(other);
        }
    }

    /// <summary>
    /// Whether <paramref name="hwnd"/> hides what is behind it, and where: Chromium's IsWindowVisibleAndFullyOpaque. Not
    /// a hidden, minimized or cloaked window, nor one the mouse clicks through, a tool window (floating ones can be
    /// larger than what they show), a see-through layered one, or one with an irregular shape.
    /// </summary>
    private static bool Covers(IntPtr hwnd, out Rect32 rect)
    {
        rect = default;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & (WsExTransparent | WsExToolWindow)) != 0) return false;
        if ((exStyle & WsExLayered) != 0)
        {
            // UpdateLayeredWindow windows have no attributes to read - their pixels decide; not counted.
            if (!GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)) return false;
            if ((flags & LwaColorKey) != 0 || ((flags & LwaAlpha) != 0 && alpha != 255)) return false;
        }
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(hwnd, region) == ComplexRegion) return false;
        }
        finally
        {
            DeleteObject(region);
        }
        if (IsCloaked(hwnd)) return false;
        return FrameBounds(hwnd, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The window as drawn - without the invisible resize borders GetWindowRect includes on Windows 10 and 11.</summary>
    private static bool FrameBounds(IntPtr hwnd, out Rect32 rect) =>
        DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Rect32>()) == 0 || GetWindowRect(hwnd, out rect);

    private void Unhook()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    public void Dispose()
    {
        Unhook();
        _look.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    // ─── Win32 ────────────────────────────────────────────────────────────

    // The window events that can change what covers what, as ranges (one hook each): foreground; move / resize ended;
    // minimize started and ended; destroyed, shown, hidden; moved or resized; cloaked, uncloaked.
    private static readonly (uint Min, uint Max)[] Events =
    [
        (0x0003, 0x0003), // EVENT_SYSTEM_FOREGROUND
        (0x000B, 0x000B), // EVENT_SYSTEM_MOVESIZEEND
        (0x0016, 0x0017), // EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND
        (0x8001, 0x8003), // EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE
        (0x800B, 0x800B), // EVENT_OBJECT_LOCATIONCHANGE
        (0x8017, 0x8018), // EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED
    ];

    private const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000;
    private const uint LwaColorKey = 0x1, LwaAlpha = 0x2;
    private const int NullRegion = 1, ComplexRegion = 3, RgnAnd = 1, RgnDiff = 4;
    private const int DwmwaExtendedFrameBounds = 9, DwmwaCloaked = 14;
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate void WinEventProc(IntPtr hook, uint winEvent, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect32 value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool SetRectRgn(IntPtr region, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
