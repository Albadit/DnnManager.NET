using System.Runtime.InteropServices;

namespace DnnManager.Presentation;

/// <summary>
/// Starts a program as the signed-in user - not with DNN Manager's administrator rights: handed to the desktop's shell
/// (Explorer, which runs as the user), as Windows' own "run as the user" does. For programs DNN Manager opens for the
/// user (an editor), which have no need of its rights - and which, installed in the user's own folders, any program of
/// theirs could have changed.
/// </summary>
internal static class Unelevated
{
    /// <summary>
    /// Starts <paramref name="exe"/> with <paramref name="arguments"/> (quoted where needed) in
    /// <paramref name="workingDirectory"/>. False when the shell isn't there to start it (no Explorer).
    /// </summary>
    public static bool Start(string exe, IEnumerable<string> arguments, string? workingDirectory)
    {
        try
        {
            var shell = DesktopShell();
            if (shell is null) return false;
            shell.ShellExecute(exe, string.Join(' ', arguments.Select(Quote)), workingDirectory ?? "", "open", 1);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or NotSupportedException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    // A command line argument as the program reads it back (CommandLineToArgvW's rules): in quotes when it has a space or
    // a quote, with backslashes before a quote - or before the closing one - doubled.
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var text = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>The desktop's Shell.Application (IShellDispatch2), from the shell window of the desktop - Explorer's.</summary>
    private static dynamic? DesktopShell()
    {
        var windowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")); // ShellWindows
        if (windowsType is null) return null;
        dynamic windows = Activator.CreateInstance(windowsType)!;
        object location = 0; // CSIDL_DESKTOP
        object root = Type.Missing;
        // SWC_DESKTOP, SWFO_NEEDDISPATCH
        var desktop = (object?)windows.FindWindowSW(ref location, ref root, 8, out int _, 1);
        if (desktop is not IServiceProvider provider) return null;

        var topLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
        var shellBrowserId = typeof(IShellBrowser).GUID;
        if (provider.QueryService(ref topLevelBrowser, ref shellBrowserId, out var browserObject) != 0 || browserObject is not IShellBrowser browser)
            return null;
        var view = browser.QueryActiveShellView();
        var dispatch = new Guid("00020400-0000-0000-C000-000000000046"); // IDispatch
        view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref dispatch, out var folderView);
        return ((dynamic)folderView).Application;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvObject);
    }

    // The methods before QueryActiveShellView only keep their places in the interface: never called.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}
