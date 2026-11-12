using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DnnManager.Application;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views;

/// <summary>
/// The settings this app uses so far - the theme, the projects folder, comparing on choosing - in the same database as
/// the Windows app's (Documents/DnnManager/dnnmanager.db). Saved, they are used at once (LiveSettings).
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store = Program.Services.GetRequiredService<SettingsStore>();
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        _loading = true;
        var settings = _store.Read();
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == settings.Appearance.Theme.ToLowerInvariant())
                                ?? ThemeBox.Items[0];
        ProjectsBox.Text = settings.Projects.BaseDirectory;
        CompareBox.IsChecked = settings.Deploy.CompareOnOpen;
        _loading = false;
        var paths = Program.Services.GetRequiredService<AppDataPaths>();
        AboutText.Text = $"DNN Manager {typeof(SettingsView).Assembly.GetName().Version?.ToString(3)} on {Platform.Name} " +
                         $"({System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})." +
                         $"{Environment.NewLine}Your data: {paths.Root}" +
                         (UserCipher.KeyFile is { } key ? $"{Environment.NewLine}The key your saved passwords are encrypted with: {key}" : "") +
                         (Platform.IsWindows ? "" : $"{Environment.NewLine}{Platform.WhyNot(PlatformFeature.Iis)}");
    }

    private string ChosenTheme => (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    // The theme is tried at once; Save keeps it.
    private void Theme_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) App.ApplyTheme(ChosenTheme);
    }

    private async void ChooseProjects_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The projects folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) ProjectsBox.Text = path;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _store.Update(s =>
            {
                s.Appearance.Theme = ChosenTheme;
                s.Projects.BaseDirectory = (ProjectsBox.Text ?? "").Trim();
                s.Deploy.CompareOnOpen = CompareBox.IsChecked == true;
            });
            Program.Services.GetRequiredService<LiveSettings>().Apply(saved);
            SaveResult.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
            SaveResult.Text = "Saved.";
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            SaveResult.Foreground = Avalonia.Media.Brushes.IndianRed;
            SaveResult.Text = ex is SettingsException { Problems.Count: > 0 } problems ? string.Join(" ", problems.Problems) : ex.Message;
        }
    }
}
