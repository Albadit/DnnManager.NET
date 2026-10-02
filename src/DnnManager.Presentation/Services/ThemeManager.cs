using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DnnManager.Presentation.Themes;
using Microsoft.Win32;

namespace DnnManager.Presentation.Services;

public enum AppTheme { Light, Dark }

/// <summary>
/// Switches between the light and dark palette at runtime by swapping the theme dictionary merged
/// into the application resources (everything binds to it with DynamicResource), and keeps the
/// Windows title bars in step. Also the UI scale and the font size (Settings → General): the
/// <c>UiScale</c> transform every window's content and every pop-up is laid out through, and the
/// <c>Text*</c> sizes in Tokens.xaml.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event EventHandler? Changed;

    /// <param name="configured">"Light" or "Dark"; anything else (e.g. "System") follows the Windows app theme.</param>
    public static void Initialize(string? configured) =>
        Apply(Enum.TryParse<AppTheme>(configured, ignoreCase: true, out var theme) ? theme : SystemTheme());


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

    /// <summary>
    /// Gives <paramref name="window"/> a title bar matching the theme, now and whenever it's (re)created, and lays its
    /// content out at the UI scale. Call it once its content is there (after InitializeComponent).
    /// </summary>
    public static void Track(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        ApplyTitleBar(window);
        // A dialog's set width is for 100 % - it grows with the scale, or its content would be cut off.
        if (window.ResizeMode == ResizeMode.NoResize && !double.IsNaN(window.Width)) window.Width *= Scale;
        ApplyScale(window);
    }

    // ─── UI scale and font size ───────────────────────────────────────────

    /// <summary>The font size the Text* sizes in Tokens.xaml are for.</summary>
    public const double DefaultFontSize = 13;

    // Each text size in Tokens.xaml, at the default font size - the chosen one keeps their proportions.
    private static readonly (string Key, double Size)[] TextSizes =
    [
        ("TextSmall", 11), ("TextHint", 11.5), ("TextCaption", 12), ("TextBody", 13), ("TextMedium", 14),
        ("TextHeading", 15), ("TextLarge", 16), ("TextTitle", 20), ("TextDisplay", 22)
    ];

    // The caption height each custom title bar was made with, at 100 %.
    private static readonly ConditionalWeakTable<WindowChrome, object> CaptionHeights = new();

    /// <summary>The UI scale, 1 = 100 %.</summary>
    public static double Scale { get; private set; } = 1;

    /// <summary>
    /// Lays everything out at <paramref name="scalePercent"/> % with text at <paramref name="fontSize"/> px (the body
    /// text; titles and hints keep their proportions) - in every open window, at once.
    /// </summary>
    public static void ApplyLayout(int scalePercent, double fontSize)
    {
        var resources = System.Windows.Application.Current.Resources;
        Scale = Math.Clamp(scalePercent, 50, 200) / 100d;
        var transform = new ScaleTransform(Scale, Scale);
        transform.Freeze(); // shared by every window and pop-up
        resources["UiScale"] = transform;

        var ratio = Math.Clamp(fontSize, 8, 32) / DefaultFontSize;
        foreach (var (key, size) in TextSizes) resources[key] = Math.Round(size * ratio * 2) / 2; // to half pixels

        foreach (Window window in System.Windows.Application.Current.Windows) ApplyScale(window);
    }

    private static void ApplyScale(Window window)
    {
        if (window.Content is FrameworkElement root) root.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScale");
        // A drawn title bar: the part to drag the window by grows with what is drawn there.
        if (WindowChrome.GetWindowChrome(window) is { IsFrozen: false } chrome)
        {
            var height = (double)CaptionHeights.GetValue(chrome, c => c.CaptionHeight);
            chrome.CaptionHeight = height * Scale;
        }
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
