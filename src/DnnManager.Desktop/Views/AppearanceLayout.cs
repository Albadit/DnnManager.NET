using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using DnnManager.Application.Configuration;

namespace DnnManager.Desktop.Views;

/// <summary>
/// The settings appearance.uiScale and appearance.fontSize put to work on a window, as the Windows app's
/// ThemeManager.ApplyLayout: the UI scale zooms the whole window (its content in a LayoutTransformControl), the font
/// size is the window's text size, which every text without a size of its own takes.
/// </summary>
public static class AppearanceLayout
{
    public static void Apply(Window window, AppearanceSettings appearance) => Apply(window, appearance.UiScale, appearance.FontSize);

    public static void Apply(Window window, int uiScale, double fontSize)
    {
        var scale = Math.Clamp(uiScale, AppearanceSettings.MinUiScale, AppearanceSettings.MaxUiScale) / 100.0;
        var zoom = window.Content as LayoutTransformControl;
        // Only once - and not for 100 % until it was ever changed: the window stays as it is made.
        if (zoom is null && Math.Abs(scale - 1) >= 0.001)
        {
            var content = window.Content as Control;
            window.Content = null;
            zoom = new LayoutTransformControl { Child = content };
            window.Content = zoom;
        }
        if (zoom is not null) zoom.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
        window.FontSize = Math.Clamp(fontSize, AppearanceSettings.MinFontSize, AppearanceSettings.MaxFontSize);
    }

    /// <summary>The main window, when there is one.</summary>
    public static Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
