using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Pages;

/// <summary>"Show all projects info", plus quick actions on the selected project.</summary>
public partial class ProjectsPage : UserControl, IRefreshable
{
    private readonly IServiceProvider _services;
    private readonly OperationRunner _runner;
    private int _loadVersion;
    // The row under the mouse at the last right-click (null: empty space) - what the context menu is for.
    private Row? _menuRow;

    public sealed record Row(ProjectStatus Status)
    {
        public string Name => Status.Name;
        public string Url => Status.SiteUrl;
        public string Iis => Status.IisSiteExists ? Status.IisSiteState ?? "present" : "(none)";
        public string Sql => Status.SqlReachable ? $"connected :{Status.SqlPort}" : "not reachable";
        public string Database => Status.DatabaseName ?? "(unknown)";
        public string Size => $"{Status.DirectorySizeBytes / 1024d / 1024d:N1} MB";
        public string Path => Status.ProjectDirectory;
    }

    public ProjectsPage(IServiceProvider services, OperationRunner runner)
    {
        _services = services; _runner = runner;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        // Look for installed IDEs now (vswhere takes a moment) so the first right-click opens at once.
        _ = Task.Run(() => IdeLocator.Installed);
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
        if (Selected is { } row) OpenFolder(row);
    }

    // ─── Context menu ─────────────────────────────────────────────────────

    // A right-click selects the row under the mouse, like Explorer, so the menu acts on that project.
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not DataGridRow)
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

        _menuRow = (node as DataGridRow)?.Item as Row;
        if (_menuRow is not null) ProjectsGrid.SelectedItem = _menuRow;
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Opened from the keyboard (Menu key / Shift+F10) the cursor position is -1: use the selection.
        var row = e.CursorLeft < 0 ? Selected : _menuRow;
        if (row is null || ProjectsGrid.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        var details = NewMenuItem("Details…", (_, _) => ShowDetails(row));
        details.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(details);
        menu.Items.Add(NewMenuItem("Open site", (_, _) => Shell(row.Url)));
        menu.Items.Add(NewMenuItem("Open folder", (_, _) => OpenFolder(row)));
        menu.Items.Add(new Separator());

        var ides = IdeLocator.Installed;
        if (ides.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No IDE found", IsEnabled = false });
        var solution = IdeLocator.SolutionFor(row.Path);
        foreach (var ide in ides)
        {
            var header = $"Open in {ide.Name}" +
                         (ide.OpensSolution && solution is not null ? $"  ({System.IO.Path.GetFileName(solution)})" : "");
            var item = NewMenuItem(header, (_, _) => OpenInIde(ide, row));
            item.ToolTip = ide.ExePath;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(NewMenuItem("Remove…", (_, _) => Remove(row)));
    }

    private static MenuItem NewMenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    private static void OpenFolder(Row row)
    {
        if (Directory.Exists(row.Path)) Shell(row.Path);
        else Dialogs.Error($"The project folder no longer exists: {row.Path}");
    }

    private static void OpenInIde(Ide ide, Row row)
    {
        if (!Directory.Exists(row.Path))
        {
            Dialogs.Error($"The project folder no longer exists: {row.Path}");
            return;
        }
        try { IdeLocator.Open(ide, row.Path); }
        catch (Exception ex) { Dialogs.Error($"Could not start {ide.Name}: {ex.Message}"); }
    }

    private void ShowDetails(Row row)
    {
        var s = row.Status;
        var dir = s.ProjectDirectory;
        var details = new List<DetailsDialog.Detail>
        {
            new("Folder", dir),
            new("Site", s.SiteUrl),
            new("IIS site", s.IisSiteExists ? s.IisSiteState ?? "present" : "none"),
            new("SQL Server", s.SqlReachable ? $"connected (port {s.SqlPort})" : "not reachable"),
            new("Database", row.Database),
        };

        var webConfig = System.IO.Path.Combine(dir, "web.config");
        if (File.Exists(webConfig))
        {
            var conn = _services.GetRequiredService<IWebConfigService>().ReadSiteSqlServer(webConfig);
            details.Add(new("web.config connection", conn is { Success: true, Value: { } c }
                ? $"[{c.Database}] on {c.Server} (user: {(c.User.Length == 0 ? "integrated" : c.User)})"
                : conn.Error ?? "no SiteSqlServer connection"));
        }
        else
        {
            details.Add(new("web.config", "not found"));
        }

        details.Add(new("DNN version", DnnVersion(dir) ?? "unknown"));
        details.Add(new("Size", row.Size));
        if (Solutions(dir) is { Count: > 0 } solutions)
            details.Add(new("Solution", string.Join(", ", solutions)));
        if (GitBranch(dir) is { } branch)
            details.Add(new("Git branch", branch));

        var backups = _services.GetRequiredService<IProjectRepository>().Build(s.Name).BackupDirectory;
        var backupCount = Directory.Exists(backups) ? Directory.EnumerateFiles(backups).Count() : 0;
        details.Add(new("Backups", backupCount == 0 ? "none" : $"{backupCount} file{(backupCount == 1 ? "" : "s")} in {backups}"));

        DetailsDialog.Show(s.Name, details);
    }

    /// <summary>The DNN version from <c>bin\DotNetNuke.dll</c>, or null when it isn't there.</summary>
    private static string? DnnVersion(string dir)
    {
        var dll = System.IO.Path.Combine(dir, "bin", "DotNetNuke.dll");
        if (!File.Exists(dll)) return null;
        var info = FileVersionInfo.GetVersionInfo(dll);
        return info.ProductVersion ?? info.FileVersion;
    }

    private static IReadOnlyList<string> Solutions(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx"))
                .Select(f => System.IO.Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    /// <summary>The checked-out branch from <c>.git\HEAD</c>; null when the folder isn't a git repository.</summary>
    private static string? GitBranch(string dir)
    {
        var head = System.IO.Path.Combine(dir, ".git", "HEAD");
        if (!File.Exists(head)) return Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git")) ? "(git repository)" : null;
        try
        {
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..]
                : text.Length >= 7 ? $"(detached at {text[..7]})" : "(unknown)";
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) Remove(row);
    }

    private async void Remove(Row row)
    {
        // RemoveProjectUseCase asks about the database and for the final confirmation itself.
        await _runner.RunAsync($"Remove '{row.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<RemoveProjectUseCase>().ExecuteAsync(row.Name, reporter, ct));
    }

    private async void ResetIis_Click(object sender, RoutedEventArgs e)
    {
        // ResetIisUseCase asks for confirmation itself; the list refreshes when it finishes.
        await _runner.RunAsync("Reset IIS",
            (sp, reporter, ct) => sp.GetRequiredService<ResetIisUseCase>().ExecuteAsync(reporter, ct));
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {target}: {ex.Message}"); }
    }
}
