using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DnnManager.Presentation.Services;

/// <summary>
/// DNN Manager's icon in the notification area (by the clock), there while "Keep DNN Manager running when you close the
/// window" is on (Settings - General): a click opens the window, a right-click offers Open and Quit. Made with
/// Shell_NotifyIcon on a window of its own that is never shown - WinForms' NotifyIcon would add WinForms to the
/// single-file exe for this alone. It comes back when Explorer restarts (TaskbarCreated). Use it on the UI thread;
/// <see cref="Dispose"/> removes it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    // The message Explorer sends this icon's window about clicks on it (WM_APP + 1).
    private const int CallbackMessage = 0x8001;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    // With version 4, what was done is in the low word of lParam: a click (or Enter / Space on the icon), a right-click.
    private const int NinSelect = 0x400, NinKeySelect = 0x401, WmContextMenu = 0x007B;
    private const uint NiifNone = 0, NiifRespectQuietTime = 0x80;
    // The icon ApplicationIcon puts in the exe (IDI_APPLICATION's number).
    private const int AppIconId = 32512;
    private const uint ImageIcon = 1, MsgfltAllow = 1;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private const int WsExToolWindow = 0x80;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly string _tip;
    // Explorer (re)started - the taskbar is new, and the icon has to be added again.
    private readonly int _taskbarCreated;
    private bool _disposed;

    public TrayIcon(string tip)
    {
        _tip = tip;
        // A top-level window that is never shown - a message-only one wouldn't hear TaskbarCreated, which is broadcast.
        _window = new HwndSource(new HwndSourceParameters("DnnManager.TrayIcon")
        {
            Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = WsExToolWindow
        });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // DNN Manager runs elevated and Explorer doesn't: Windows drops its messages unless they are let through.
        ChangeWindowMessageFilterEx(_window.Handle, (uint)_taskbarCreated, MsgfltAllow, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_window.Handle, CallbackMessage, MsgfltAllow, IntPtr.Zero);
        _icon = LoadImage(GetModuleHandle(null), AppIconId, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), 0);
        // Not in this exe (a build without its icon): Windows' own application icon - shared, so never destroyed.
        _ownsIcon = _icon != IntPtr.Zero;
        if (!_ownsIcon) _icon = LoadIcon(IntPtr.Zero, AppIconId);
        Add();
    }

    /// <summary>The icon was clicked, or "Open DNN Manager" chosen on its menu.</summary>
    public event EventHandler? Open;

    /// <summary>"Quit DNN Manager" was chosen on its menu.</summary>
    public event EventHandler? Quit;

    /// <summary>A Windows notification from the icon - for what happens while the window is hidden.</summary>
    public void ShowNotice(string title, string text)
    {
        if (_disposed) return;
        var data = Data(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NiifNone | NiifRespectQuietTime;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        // No Explorer (yet): it is added when the taskbar is created.
        if (!Shell_NotifyIcon(NimAdd, ref data)) return;
        data.uVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            Add();
        }
        else if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NinSelect or NinKeySelect:
                    Open?.Invoke(this, EventArgs.Empty);
                    break;
                case WmContextMenu:
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Open and Quit, where the pointer is - in the app's own menu style, as its other menus.</summary>
    private void ShowMenu()
    {
        var open = new MenuItem { Header = "Open DNN Manager", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => Open?.Invoke(this, EventArgs.Empty);
        var quit = new MenuItem { Header = "Quit DNN Manager" };
        quit.Click += (_, _) => Quit?.Invoke(this, EventArgs.Empty);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Items = { open, new Separator(), quit } };
        // A menu of an app that isn't in front doesn't close on a click elsewhere: put its own window in front (Explorer
        // lets this app take the foreground after a click on its icon).
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
        };
        menu.IsOpen = true;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        if (_ownsIcon) DestroyIcon(_icon);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        // uTimeout in the same place - long ignored by Windows.
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr instance, nint name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, nint name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
