using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DnnManager.Presentation.Themes;
using Microsoft.Win32;

namespace DnnManager.Presentation.Services;

public enum AppTheme { Light, Dark }

/// <summary>
/// Switches between the light and dark palette at runtime by swapping the theme dictionary merged
/// into the application resources (everything binds to it with DynamicResource), and keeps the
/// Windows title bars in step.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event EventHandler? Changed;

    /// <param name="configured">"Light" or "Dark"; anything else (e.g. "System") follows the Windows app theme.</param>
    public static void Initialize(string? configured) =>
        Apply(Enum.TryParse<AppTheme>(configured, ignoreCase: true, out var theme) ? theme : SystemTheme());

    public static void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);

    public static void Apply(AppTheme theme)
    {
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        ResourceDictionary palette = theme == AppTheme.Dark ? new DarkTheme() : new LightTheme();
        var index = -1;
        for (var i = 0; i < merged.Count; i++)
            if (merged[i] is LightTheme or DarkTheme) { index = i; break; }
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette);

        Current = theme;
        foreach (Window window in System.Windows.Application.Current.Windows)
            ApplyTitleBar(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Gives <paramref name="window"/> a title bar matching the theme, now and whenever it's (re)created.</summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        ApplyTitleBar(window);
    }

    private static AppTheme SystemTheme()
    {
        var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1);
        return value is 0 ? AppTheme.Dark : AppTheme.Light;
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE - Windows 10 20H1+ / Windows 11. Older builds just keep a light title bar.
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // not created yet - SourceInitialized applies it
        var dark = Current == AppTheme.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int)); }
        catch { /* cosmetic only */ }
    }
}
