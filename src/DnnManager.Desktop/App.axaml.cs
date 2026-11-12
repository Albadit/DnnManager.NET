using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DnnManager.Desktop.Views;

namespace DnnManager.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(Program.Settings.Appearance.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The setting appearance.theme: system (follows the OS), light or dark.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
