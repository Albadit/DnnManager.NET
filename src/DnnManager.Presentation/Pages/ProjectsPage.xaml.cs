using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Pages;

/// <summary>"Show all projects info", plus quick actions on the selected project.</summary>
public partial class ProjectsPage : UserControl, IRefreshable
{
    private readonly IServiceProvider _services;
    private readonly OperationRunner _runner;
    private int _loadVersion;

    public sealed record Row(ProjectStatus Status)
    {
        public string Name => Status.Name;
        public string Url => Status.SiteUrl;
        public string Iis => Status.IisSiteExists ? Status.IisSiteState ?? "present" : "(none)";
        public string Sql => Status.ContainerRunning ? $"running :{Status.SqlPort}" : "not running";
        public string Database => Status.DatabaseName ?? "(unknown)";
        public string Size => $"{Status.DirectorySizeBytes / 1024d / 1024d:N1} MB";
        public string Path => Status.ProjectDirectory;
    }

    public ProjectsPage(IServiceProvider services, OperationRunner runner)
    {
        _services = services; _runner = runner;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private Row? Selected => ProjectsGrid.SelectedItem as Row;

    public async void Refresh()
    {
        // Sizing every site walks a lot of files; ignore a slower, older load that finishes late.
        var version = ++_loadVersion;
        SetLoading(true);
        try
        {
            var list = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ListProjectsUseCase>().ExecuteAsync(CancellationToken.None);
            });
            if (version != _loadVersion) return;

            var selected = Selected?.Name;
            ProjectsGrid.ItemsSource = list.Select(p => new Row(p)).ToList();
            ProjectsGrid.SelectedItem = ProjectsGrid.Items.OfType<Row>().FirstOrDefault(r => r.Name == selected);
            Subtitle.Text = $"{list.Count} project folder{(list.Count == 1 ? "" : "s")} - their IIS site and database. " +
                            $"Updated {DateTime.Now:HH:mm:ss}.";
            ShowOverlay(list.Count == 0 ? "No projects found." : null);
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            ShowOverlay($"Could not load projects: {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion) SetLoading(false);
        }
    }

    /// <summary>
    /// Makes a (re)load visible: the button reads "Refreshing…", a bar runs along the table and the current
    /// rows fade until the new list arrives. The centred message is only for an empty table.
    /// </summary>
    private void SetLoading(bool loading)
    {
        RefreshButton.IsEnabled = !loading;
        RefreshButton.Content = loading ? "Refreshing…" : "Refresh";
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ProjectsGrid.Opacity = loading ? 0.5 : 1;
        if (loading && ProjectsGrid.Items.Count == 0) ShowOverlay("Loading projects…");
    }

    private void ShowOverlay(string? text)
    {
        OverlayText.Text = text ?? "";
        Overlay.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var any = Selected is not null;
        OpenSiteButton.IsEnabled = any;
        OpenFolderButton.IsEnabled = any;
        RemoveButton.IsEnabled = any;
    }

    // WPF has no sideways wheel scrolling: Shift + wheel scrolls the table horizontally.
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || FindScrollViewer(ProjectsGrid) is not { } viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) OpenSite_Click(sender, e);
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) Shell(row.Url);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && Directory.Exists(row.Path)) Shell(row.Path);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        // RemoveProjectUseCase asks about the database and for the final confirmation itself.
        await _runner.RunAsync($"Remove '{row.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<RemoveProjectUseCase>().ExecuteAsync(row.Name, reporter, ct));
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {target}: {ex.Message}"); }
    }
}
