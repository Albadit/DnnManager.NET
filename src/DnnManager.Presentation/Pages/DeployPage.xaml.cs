using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// The Deploy page - VS Code's Source Control for a project's servers. The side bar lists every project's servers (its
/// repositories); for the chosen one, what differs - staged, to push, to pull, conflicts - each file with its status
/// letter, staged with + and pushed or pulled with a message (Push, its ▾ the rest), after a review of every operation;
/// and the history (its graph), each operation rolled back from here. The editor shows the chosen file's diff, with a
/// conflict's choices above it, or the chosen operation. Everything that touches a server or a project's files runs as
/// an operation (<see cref="OperationRunner"/>): in the Output tab, cancellable, undone when it fails.
/// </summary>
public partial class DeployPage : UserControl, IRefreshable, ISideBarPage
{
    private readonly IServiceProvider _services;
    private readonly ServerStore _store;
    private readonly DeployConnections _connections;
    private readonly DeployFiles _files;
    private readonly Dictionary<Guid, SyncScan> _scans = [];
    private readonly HashSet<Guid> _autoScanned = [];
    private readonly HashSet<string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ChangeGroup> _collapsed = [];
    private SyncScan? _scan;
    private CancellationTokenSource? _diffLoad;
    private TransferUpdate? _lastUpdate;
    private bool _updatePending;

    public DeployPage(IServiceProvider services, ServerStore store)
    {
        _services = services;
        _store = store;
        _connections = services.GetRequiredService<DeployConnections>();
        _files = services.GetRequiredService<DeployFiles>();
        InitializeComponent();
        // The servers, changes and history go to the window's primary side bar; the page keeps the editor.
        Root.Children.Remove(SideGrid);
        LoadServers(null);
        // The projects come from IIS after the window opens (and come and go later): the servers follow them - a page made
        // before IIS answered would otherwise stay empty.
        _store.Projects.CollectionChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
            else _serversStale = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Activated();
        };
    }

    private ServerRow? Selected => ServersList.SelectedItem as ServerRow;

    private OperationRunner Runner => _services.GetRequiredService<OperationRunner>();

    public void Refresh() => LoadServers(Selected?.Connection.Id);

    // The project list changed since the servers were listed.
    private bool _serversStale;

    // ─── The side bar's view ───────────────────────────────────────────────

    /// <summary>The servers, the message and Push, the changes and the history - in the window's primary side bar.</summary>
    public FrameworkElement SideBar => SideGrid;

    // The history's drag: where the pointer started, and the history's height then.
    private double _dragFrom;
    private double _dragSize;

    private void HistorySash_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _dragFrom = Mouse.GetPosition(SideGrid).Y;
        _dragSize = HistoryRow.ActualHeight;
        // Dragged open from its title.
        if (HistoryToggle.IsChecked != true)
        {
            HistoryToggle.IsChecked = true;
            HistoryToggle_Click(this, new RoutedEventArgs());
        }
    }

    private void HistorySash_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // Up: the history taller; never so tall the changes have no room.
        var height = _dragSize - (Mouse.GetPosition(SideGrid).Y - _dragFrom);
        var room = SideGrid.ActualHeight - SideGrid.RowDefinitions.Take(5).Sum(r => r.ActualHeight) - 80;
        HistoryRow.Height = new GridLength(Math.Clamp(height, HistoryRow.MinHeight, Math.Max(HistoryRow.MinHeight, room)));
        _historyHeight = HistoryRow.Height.Value;
    }

    // The history's height while it is open - kept while it is folded.
    private double _historyHeight = 220;

    private void HistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = HistoryToggle.IsChecked == true;
        HistoryBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HistoryRow.MinHeight = open ? 32 : 0;
        HistoryRow.Height = open ? new GridLength(_historyHeight) : GridLength.Auto;
    }

    /// <summary>
    /// Shown: the chosen server is compared once, by itself - so what was changed on it directly shows without asking -
    /// unless another operation is running.
    /// </summary>
    public void Activated()
    {
        if (_serversStale) Refresh();
        if (_connections.CompareOnOpen && Selected is { } server && !_scans.ContainsKey(server.Connection.Id) && _autoScanned.Add(server.Connection.Id) &&
            !Runner.IsBusy)
            _ = ScanAsync();
    }

    /// <summary>Opens the page on <paramref name="site"/>'s first server - or, without one, offers to add it.</summary>
    public void ShowProject(string site)
    {
        LoadServers(null);
        if (((IEnumerable<ServerRow>)ServersList.ItemsSource).FirstOrDefault(s => s.Site.Equals(site, StringComparison.OrdinalIgnoreCase)) is { } first)
            ServersList.SelectedItem = first;
        else if (_store.Projects.FirstOrDefault(p => p.Name.Equals(site, StringComparison.OrdinalIgnoreCase)) is { } row)
            AddServer(row);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        CompareOnF5(e);
    }

    // The side bar is in the window's tree, not the page's: F5 there too.
    private void SideGrid_PreviewKeyDown(object sender, KeyEventArgs e) => CompareOnF5(e);

    private void CompareOnF5(KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.F5 || Selected is null) return;
        _ = ScanAsync();
        e.Handled = true;
    }

    // ─── Servers ───────────────────────────────────────────────────────────

    /// <summary>Every project's servers, by project - the projects IIS has now, with their folders.</summary>
    private void LoadServers(Guid? select)
    {
        _serversStale = false;
        var rows = new List<ServerRow>();
        foreach (var project in _store.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var connection in _connections.For(project.Name))
                rows.Add(new ServerRow(connection, project.Path, _scans.GetValueOrDefault(connection.Id)));
        var keep = select ?? Selected?.Connection.Id;
        ServersList.ItemsSource = rows;
        ServersList.SelectedItem = rows.FirstOrDefault(r => r.Connection.Id == keep) ?? rows.FirstOrDefault();
        var any = rows.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        FileView.Visibility = any && OperationView.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        CommitArea.IsEnabled = ScanButton.IsEnabled = any;
        if (!any) ShowScan();
    }

    private void Server_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || e.AddedItems.Count == 0 && Selected is not null) return;
        _staged.Clear();
        _scan = Selected is { } s ? _scans.GetValueOrDefault(s.Connection.Id) : null;
        ShowFile(null);
        ShowScan();
        LoadHistory();
        if (IsVisible) Activated();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Which project: the chosen server's, or one picked from the list.
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Add a server to…", IsEnabled = false });
        foreach (var project in _store.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem { Header = project.Name, ToolTip = project.Path };
            item.Click += (_, _) => AddServer(project);
            menu.Items.Add(item);
        }
        if (_store.Projects.Count == 0) menu.Items.Add(new MenuItem { Header = "No project yet - make one with New project", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void AddServer(ProjectRow project)
    {
        if (DeployConnectionDialog.Show(_connections, project.Name, null) is { } added) LoadServers(added.Id);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s || DeployConnectionDialog.Show(_connections, s.Site, _connections.Find(s.Connection.Id) ?? s.Connection) is not { } edited) return;
        // Other rules or another folder: what was compared isn't what would be pushed.
        _scans.Remove(edited.Id);
        _scan = null;
        LoadServers(edited.Id);
        ShowScan();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s || !EnsurePassword(s.Connection)) return;
        var current = _connections.Find(s.Connection.Id) ?? s.Connection;
        var check = await Task.Run(() => _connections.TestAsync(current, _connections.Secrets(current.Id), CancellationToken.None));
        _connections.RecordCheck(current.Id, check);
        LoadServers(current.Id);
        Toast.Show(check.NeedsTrust ? $"{check.Message} Edit the connection to check and trust it." : check.Message,
            check.Ok ? ToastKind.Success : ToastKind.Warning);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        if (!Dialogs.ConfirmDanger($"Remove the connection '{s.Name}' of '{s.Site}' ({s.Connection.Host})?{Environment.NewLine}{Environment.NewLine}" +
                                   "Its saved password or key, its baseline and its history of pushes and pulls are deleted. No file is deleted - " +
                                   "not in the project, not on the server.", "Remove connection", "Keep it"))
            return;
        _connections.Delete(s.Connection.Id);
        _scans.Remove(s.Connection.Id);
        _scan = null;
        LoadServers(null);
        ShowScan();
        LoadHistory();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            return item;
        }
        var scanned = _scan is not null;
        menu.Items.Add(Item("Stage all to push", () => StageAll(ChangeGroup.Outgoing), scanned));
        menu.Items.Add(Item("Stage all to pull", () => StageAll(ChangeGroup.Incoming), scanned));
        menu.Items.Add(Item("Unstage all", () => { _staged.Clear(); Fill(); }, _staged.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Edit connection…", () => Edit_Click(this, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(Item("Test connection", () => Test_Click(this, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(Item("Remove connection…", () => Remove_Click(this, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Source Control settings…", () => _services.GetRequiredService<AppCommands>().All.FirstOrDefault(c => c.Id == "settings.deploy")?.Run?.Invoke()));
        menu.IsOpen = true;
    }

    // ─── Compare ───────────────────────────────────────────────────────────

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    /// <summary>"Ask for password": typed once, the first time the server is reached this session - false when it wasn't given.</summary>
    private bool EnsurePassword(DeployConnection stale)
    {
        var connection = _connections.Find(stale.Id) ?? stale;
        if (!_connections.NeedsPassword(connection)) return true;
        if (InputDialog.ShowSecret($"Password for {connection.UserName} on {connection.Host} ({connection.Name}) - kept until DNN Manager closes:",
                "Connect") is not { } password)
            return false;
        _connections.RememberForSession(connection.Id, password);
        return true;
    }

    private async Task ScanAsync()
    {
        if (Selected is not { } server || _connections.Find(server.Connection.Id) is not { } connection) return;
        if (!EnsurePassword(connection)) return;
        SyncScan? result = null;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        try
        {
            await Runner.RunAsync($"Compare '{connection.Site}' with {connection.Name} ({connection.Environment})", async (sp, reporter, ct) =>
            {
                try
                {
                    result = await sp.GetRequiredService<SyncScanner>().ScanAsync(connection, server.Root, reporter, ct);
                    return Result.Ok();
                }
                catch (Exception ex) when (ex is RemoteException or IOException or UnauthorizedAccessException)
                {
                    // The server's health says it until it works again.
                    if (ex is RemoteException) _connections.RecordCheck(connection.Id, new ConnectionCheck(false, ex.Message));
                    return Result.Fail(Explain(ex));
                }
            });
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Hidden;
        }
        if (result is not null) _scans[connection.Id] = result;
        LoadServers(connection.Id);
        if (Selected?.Connection.Id != connection.Id || result is null) return;
        _scan = result;
        // What is staged stays staged while it still has the same way to go.
        _staged.RemoveWhere(p => result.Find(p) is not { Direction: SyncDirection.Push or SyncDirection.Pull });
        ShowScan();
    }

    private static string Explain(Exception ex) => ex switch
    {
        UntrustedServerException { FirstTime: true } => $"{ex.Message} Edit the connection and test it to check and trust it.",
        UntrustedServerException => $"{ex.Message} Edit the connection and test it to see the new identity.",
        _ => ex.Message
    };

    private void ShowScan()
    {
        if (_scan is null)
        {
            ScanSummary.Text = "";
            Changes.ItemsSource = null;
            ChangesMessage.Text = Selected is null ? "" : "Not compared yet - Compare (F5) to see what differs.";
            ChangesMessage.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateButtons();
            return;
        }
        ScanSummary.Text = $"compared {_scan.ScannedUtc.ToLocalTime():t}" + (_scan.Warnings.Count > 0 ? $" · {_scan.Warnings.Count} left out" : "");
        ScanSummary.ToolTip = _scan.Warnings.Count > 0 ? string.Join(Environment.NewLine, _scan.Warnings.Take(30)) : null;
        Fill();
    }

    /// <summary>The changes in their groups - staged, to push, to pull, conflicts - as one list, each group's files by path.</summary>
    private void Fill()
    {
        if (_scan is null) return;
        var chosen = (Changes.SelectedItem as ChangeRow)?.Path;
        var rows = new List<ChangeRow>();
        var changes = _scan.Changes.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        void Group(ChangeGroup group, string title, IReadOnlyList<SyncItem> items, bool staged)
        {
            if (items.Count == 0) return;
            var collapsed = _collapsed.Contains(group);
            rows.Add(new ChangeRow
            {
                Group = group, Title = title, Count = items.Count, Collapsed = collapsed,
                CanStageAll = !staged && group != ChangeGroup.Conflicts, CanUnstageAll = staged
            });
            if (!collapsed)
                rows.AddRange(items.Select(i => new ChangeRow { Group = group, Item = i, IsStaged = staged }));
        }
        Group(ChangeGroup.Staged, "Staged Changes", [.. changes.Where(i => _staged.Contains(i.Path))], true);
        Group(ChangeGroup.Outgoing, "Changes - to push", [.. changes.Where(i => i.Direction == SyncDirection.Push && !_staged.Contains(i.Path))], false);
        Group(ChangeGroup.Incoming, "Incoming - to pull", [.. changes.Where(i => i.Direction == SyncDirection.Pull && !_staged.Contains(i.Path))], false);
        Group(ChangeGroup.Conflicts, "Conflicts - changed in both", [.. changes.Where(i => i.Direction == SyncDirection.Conflict)], false);
        Changes.ItemsSource = rows;
        if (chosen is not null) Changes.SelectedItem = rows.FirstOrDefault(r => !r.IsHeader && r.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        ChangesMessage.Text = changes.Count == 0 ? "The project and the server are the same - nothing to push or pull." : "";
        ChangesMessage.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    // ─── Staging ───────────────────────────────────────────────────────────

    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangeRow { Item: { } item }) Stage([item], true);
    }

    private void Unstage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangeRow { Item: { } item }) Stage([item], false);
    }

    private void StageGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangeRow row) StageAll(row.Group);
    }

    private void UnstageGroup_Click(object sender, RoutedEventArgs e)
    {
        _staged.Clear();
        Fill();
    }

    /// <summary>Every file of a group - never one that looks like a secret: each of those is staged on its own, on purpose.</summary>
    private void StageAll(ChangeGroup group)
    {
        if (_scan is null) return;
        var direction = group == ChangeGroup.Incoming ? SyncDirection.Pull : SyncDirection.Push;
        Stage([.. _scan.Changes.Where(i => i.Direction == direction && !i.Sensitive)], true);
    }

    private void Stage(IEnumerable<SyncItem> items, bool stage)
    {
        foreach (var item in items.Where(i => i.Direction is SyncDirection.Push or SyncDirection.Pull))
        {
            if (stage) _staged.Add(item.Path);
            else _staged.Remove(item.Path);
        }
        Fill();
    }

    private void Changes_KeyDown(object sender, KeyEventArgs e)
    {
        if (Changes.SelectedItem is not ChangeRow row) return;
        if (e.Key == Key.Space)
        {
            // Space stages or unstages, as VS Code's + and -; on a header, opens or closes the group.
            if (row.IsHeader) Toggle(row.Group);
            else if (row.Item is { } item) Stage([item], !row.IsStaged);
            e.Handled = true;
        }
    }

    private void Changes_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { IsHeader: true } row) Toggle(row.Group);
    }

    private void Toggle(ChangeGroup group)
    {
        if (!_collapsed.Remove(group)) _collapsed.Add(group);
        Fill();
    }

    // The folder of the row the menu was opened on: stage or unstage everything in it.
    private string? MenuFolder => (Changes.SelectedItem as ChangeRow) is { IsHeader: false } row && row.Folder.Length > 0 ? row.Folder : null;

    private void ChangesMenu_Opened(object sender, RoutedEventArgs e)
    {
        var folder = MenuFolder;
        StageFolderItem.Header = folder is null ? "Stage everything in the folder" : $"Stage everything in {folder}/";
        UnstageFolderItem.Header = folder is null ? "Unstage everything in the folder" : $"Unstage everything in {folder}/";
        StageFolderItem.IsEnabled = UnstageFolderItem.IsEnabled = folder is not null;
    }

    private IEnumerable<SyncItem> InFolder(string folder) =>
        _scan?.Changes.Where(i => i.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) ?? [];

    private void StageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFolder is { } folder) Stage(InFolder(folder).Where(i => !i.Sensitive), true);
    }

    private void UnstageFolder_Click(object sender, RoutedEventArgs e)
    {
        if (MenuFolder is { } folder) Stage(InFolder(folder), false);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { IsHeader: false } row)
            try { Clipboard.SetText(row.Path); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { IsHeader: false } row && Selected is { } s && DeployPath.Local(s.Root, row.Path) is { } file)
            Shell.Open(File.Exists(file) ? Path.GetDirectoryName(file)! : s.Root);
    }

    private IReadOnlyList<SyncItem> StagedItems(SyncDirection direction) =>
        _scan is null ? [] : [.. _staged.Select(p => _scan.Find(p)).OfType<SyncItem>().Where(i => i.Direction == direction)];

    /// <summary>Push when files to push are staged, Pull when only files to pull are - VS Code's Commit button.</summary>
    private void UpdateButtons()
    {
        var push = StagedItems(SyncDirection.Push).Count;
        var pull = StagedItems(SyncDirection.Pull).Count;
        var pulling = push == 0 && pull > 0;
        PrimaryLabel.Text = pulling ? $"Pull {pull}" : push > 0 ? $"Push {push}" : "Push";
        PrimaryGlyph.Text = (string)FindResource(pulling ? "GlyphDownload" : "GlyphUpload");
        PrimaryButton.IsEnabled = push + pull > 0;
        PrimaryButton.ToolTip = pulling
            ? "Pull the staged files - from the server into the project, after a review of each"
            : "Push the staged files - from the project to the server, after a review of each (Ctrl+Enter)";
        CommitMessage.Tag = pulling ? "Message (Ctrl+Enter to pull)" : "Message (Ctrl+Enter to push)";
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (StagedItems(SyncDirection.Push).Count > 0) _ = PushAsync();
        else if (StagedItems(SyncDirection.Pull).Count > 0) _ = PullAsync(false);
    }

    private void CommitMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        if (PrimaryButton.IsEnabled) Primary_Click(this, new RoutedEventArgs());
    }

    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        var push = StagedItems(SyncDirection.Push).Count;
        var pull = StagedItems(SyncDirection.Pull).Count;
        var menu = new ContextMenu { PlacementTarget = ActionsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action run, bool enabled)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => run();
            return item;
        }
        menu.Items.Add(Item(push > 0 ? $"Push {push} staged" : "Push", () => _ = PushAsync(), push > 0));
        menu.Items.Add(Item(pull > 0 ? $"Pull {pull} staged" : "Pull", () => _ = PullAsync(false), pull > 0));
        menu.Items.Add(Item(pull > 0 ? $"Pull {pull} staged into a review folder" : "Pull into a review folder", () => _ = PullAsync(true), pull > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Compare again", () => _ = ScanAsync(), Selected is not null));
        menu.IsOpen = true;
    }

    // ─── Diff ──────────────────────────────────────────────────────────────

    private void Change_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { Item: { } item }) _ = ShowFileAsync(item);
    }

    /// <summary>The editor shows the file view - nothing chosen when <paramref name="item"/> is null.</summary>
    private void ShowFile(SyncItem? item)
    {
        OperationView.Visibility = Visibility.Collapsed;
        FileView.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.SelectedItem = null;
        FileTab.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null)
        {
            FileLetter.Text = FileTitle.Text = FileSubtitle.Text = "";
            ConflictBar.Visibility = Visibility.Collapsed;
            Diff.ShowMessage("Choose a file to see what changed.");
            return;
        }
        FileLetter.Text = ChangeRow.LetterOf(item);
        FileLetter.SetResourceReference(ForegroundProperty, FileLetter.Text switch { "A" => "SuccessText", "D" => "ErrorText", "!" => "DeployConflict", _ => "LogWarn" });
        FileTitle.Text = DeployPath.Name(item.Path);
        FileSubtitle.Text = $"{DeployPath.Folder(item.Path)} · {item.Describe().ToLowerInvariant()}";
        ConflictBar.Visibility = item.Direction == SyncDirection.Conflict ? Visibility.Visible : Visibility.Collapsed;
        ConflictText.Text = $"{item.Path}: {item.Describe().ToLowerInvariant()} since they last agreed. Nothing is pushed or pulled until you choose which " +
                            "version stays - the server only changes when you push.";
        MergeButton.IsEnabled = item is { Local: not null, Remote: not null };
    }

    private async Task ShowFileAsync(SyncItem item)
    {
        _diffLoad?.Cancel();
        if (_scan is not { } scan) return;
        ShowFile(item);
        if (item.Remote is not null && !EnsurePassword(scan.Connection))
        {
            Diff.ShowMessage("The server's password wasn't given - its version can't be read.");
            return;
        }
        var load = _diffLoad = new CancellationTokenSource();
        Diff.ShowMessage($"Reading {item.Path}…");
        try
        {
            var versions = await Task.Run(() => _files.VersionsAsync(scan, item, load.Token), load.Token);
            if (load.IsCancellationRequested) return;
            if (versions.TooLarge)
            {
                Diff.ShowMessage($"{item.Path} is larger than {TextContent.MaxBytes / (1024 * 1024)} MB - too big to show a diff of.");
                return;
            }
            const string project = "In the project", server = "On the server";
            if (item.Direction == SyncDirection.Pull) Diff.ShowDiff(item.Path, versions.Local, versions.Remote, project, server);
            else Diff.ShowDiff(item.Path, versions.Remote, versions.Local, server, project);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is RemoteException or IOException or UnauthorizedAccessException)
        {
            if (!load.IsCancellationRequested) Diff.ShowMessage($"Couldn't read {item.Path}: {Explain(ex)}");
        }
    }

    // ─── Conflicts ─────────────────────────────────────────────────────────

    private SyncItem? ChosenConflict => Changes.SelectedItem is ChangeRow { Item: { Direction: SyncDirection.Conflict } item } ? item : null;

    private void KeepLocal_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenConflict is { } item) _ = ResolveAsync(item, ConflictChoice.KeepLocal, null, null);
    }

    private void TakeRemote_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenConflict is not { } item) return;
        var what = item.Remote is null
            ? $"The server deleted {item.Path}: it is deleted from the project too."
            : $"The server's {item.Path} replaces the project's.";
        if (!Dialogs.Confirm($"{what}{Environment.NewLine}{Environment.NewLine}The project's version is kept first - the history rolls it back.",
                item.Remote is null ? "Delete it here too" : "Take the server's", "Cancel"))
            return;
        _ = ResolveAsync(item, ConflictChoice.TakeRemote, null, null);
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenConflict is not { } item || _scan is not { } scan) return;
        FileVersions versions;
        try
        {
            versions = await Task.Run(() => _files.VersionsAsync(scan, item, CancellationToken.None));
        }
        catch (Exception ex) when (ex is RemoteException or IOException)
        {
            Dialogs.Error($"Couldn't read {item.Path}: {Explain(ex)}");
            return;
        }
        if (versions.Local is null || versions.Remote is null || versions.IsBinary || versions.TooLarge)
        {
            Dialogs.Error($"{item.Path} can't be merged as text - keep the project's version or take the server's.");
            return;
        }
        if (MergeDialog.Show(item.Path, versions.Base, versions.Local, versions.Remote) is { } merged)
            await ResolveAsync(item, ConflictChoice.Merged, merged, versions.RemoteHash);
    }

    private async Task ResolveAsync(SyncItem item, ConflictChoice choice, byte[]? merged, string? remoteHash)
    {
        if (_scan is not { } scan) return;
        var request = new ResolveRequest(scan, item.Path, choice, merged, remoteHash);
        if (await Run($"Resolve the conflict of {item.Path}", (sp, reporter, ct) => sp.GetRequiredService<ResolveConflictUseCase>().ExecuteAsync(request, reporter, ct)))
            await ScanAsync();
    }

    // ─── Push and pull ─────────────────────────────────────────────────────

    private async Task PushAsync()
    {
        if (_scan is not { } scan || _connections.Find(scan.Connection.Id) is not { } connection) return;
        var items = StagedItems(SyncDirection.Push);
        if (items.Count == 0) return;
        if (DeployPreviewDialog.Show(true, connection, items, CommitMessage.Text) is not { } consent) return;
        var request = new PushRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Push"), consent.AllowSensitive, Progress);
        if (await Run($"Push {items.Count} file(s) of '{connection.Site}' to {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PushChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            Toast.Show($"Pushed {items.Count} file(s) to {connection.Name}.", ToastKind.Success);
            AfterCommit(items);
        }
        await ScanAsync();
    }

    private async Task PullAsync(bool intoReview)
    {
        if (_scan is not { } scan || _connections.Find(scan.Connection.Id) is not { } connection) return;
        var items = StagedItems(SyncDirection.Pull);
        if (items.Count == 0) return;
        if (DeployPreviewDialog.Show(false, connection, items, CommitMessage.Text, intoReview) is not { } consent) return;
        var review = consent.IntoReviewFolder
            ? Path.Combine(_services.GetRequiredService<AppDataPaths>().DeploymentsDirectory, $"{connection.Site}_review_{DateTime.Now:yyyyMMdd_HHmmss}")
            : null;
        var request = new PullRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Pull"), consent.AllowSensitive, review, Progress);
        if (await Run($"Pull {items.Count} file(s) of '{connection.Site}' from {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PullChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            if (review is not null)
                Toast.Show($"The server's {items.Count} file(s) are in a review folder - the project didn't change.", ToastKind.Success, "Open folder",
                    () => Shell.Open(review));
            else
                Toast.Show($"Pulled {items.Count} file(s) from {connection.Name}.", ToastKind.Success);
            AfterCommit(items);
        }
        await ScanAsync();
    }

    private string Message(IReadOnlyList<SyncItem> items, string verb) =>
        CommitMessage.Text.Trim().Length > 0 ? CommitMessage.Text.Trim()
            : items.Count == 1 ? $"{verb} {items[0].Path}" : $"{verb} {items.Count} files";

    private void AfterCommit(IReadOnlyList<SyncItem> done)
    {
        foreach (var item in done) _staged.Remove(item.Path);
        CommitMessage.Text = "";
        LoadHistory();
    }

    /// <summary>Runs a use case as an operation - its progress shown here, its failure as the runner shows any.</summary>
    private async Task<bool> Run(string title, Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result<DeployOperation>>> work)
    {
        if (Selected is { } server && !EnsurePassword(server.Connection)) return false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        ProgressText.Text = title;
        ProgressText.Visibility = Visibility.Visible;
        try
        {
            return await Runner.RunAsync(title, async (sp, reporter, ct) =>
            {
                try
                {
                    return (await work(sp, reporter, ct)).WithoutValue();
                }
                catch (RemoteException ex)
                {
                    return Result.Fail(Explain(ex));
                }
            });
        }
        finally
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Hidden;
            ProgressText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>A transfer's progress, from the thread pool: the latest is shown, at most a few times a second.</summary>
    private void Progress(TransferUpdate update)
    {
        _lastUpdate = update;
        if (_updatePending) return;
        _updatePending = true;
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(150);
            _updatePending = false;
            if (_lastUpdate is not { } last) return;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = Math.Max(1, last.FilesTotal);
            ProgressBar.Value = last.FilesDone;
            var file = last.Total > 0 && last.Done < last.Total ? $" ({ByteSize.Format(last.Done)} of {ByteSize.Format(last.Total)})" : "";
            ProgressText.Text = $"{last.Phase} {last.Path}{file} · {last.FilesDone} of {last.FilesTotal}";
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ─── History ───────────────────────────────────────────────────────────

    private void LoadHistory()
    {
        if (Selected is not { } s)
        {
            HistoryList.ItemsSource = null;
            HistoryMessage.Visibility = Visibility.Collapsed;
            return;
        }
        var rows = _files.History(s.Connection.Id).Select(o => new OperationRow(o)).ToList();
        HistoryList.ItemsSource = rows;
        HistoryMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private DeployOperation? ChosenOperation => (HistoryList.SelectedItem as OperationRow)?.Operation;

    private void Operation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ChosenOperation is not { } op) return;
        Changes.SelectedItem = null;
        FileView.Visibility = Visibility.Collapsed;
        OperationView.Visibility = Visibility.Visible;
        var row = new OperationRow(op);
        OperationTitle.Text = $"{row.Arrow} #{op.Id} {op.Message}";
        OperationText.Text = $"{op.Kind} on {op.ConnectionName} ({op.Environment}) by {op.User}, {op.StartedUtc.ToLocalTime():g} - {row.Result}.";
        OperationFiles.ItemsSource = _files.Files(op.Id).Select(f => new OperationFileRow(f)).ToList();
        RollbackButton.IsEnabled = op is { Status: DeployOperationStatus.Completed, RolledBackBy: null, ReviewFolder: null };
        OpenReviewButton.Visibility = op.ReviewFolder is not null ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled = false;
        HistoryDiff.ShowMessage("Choose a file to see what the operation changed.");
        if (OperationFiles.Items.Count > 0) OperationFiles.SelectedIndex = 0;
    }

    private async void OperationFile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OperationFiles.SelectedItem is not OperationFileRow { File: var file })
        {
            RestoreButton.IsEnabled = false;
            return;
        }
        RestoreButton.IsEnabled = file.BeforeHash is not null && _files.IsKept(file.BeforeHash) && ChosenOperation?.ReviewFolder is null;
        var before = await _files.KeptAsync(file.BeforeHash, CancellationToken.None);
        var after = await _files.KeptAsync(file.AfterHash, CancellationToken.None);
        if (file.BeforeHash is not null && before is null || file.AfterHash is not null && after is null)
            HistoryDiff.ShowMessage("A version of this file isn't kept any more (or isn't text, or is too big to show).");
        else
            HistoryDiff.ShowDiff(file.Path, before, after, "Before", "After");
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenOperation is not { } op || Selected is not { } s) return;
        var where = op.Kind == DeployOperationKind.Push ? $"on {s.Name} ({s.Connection.Environment})" : "in the project";
        if (!Dialogs.ConfirmDanger($"Roll back #{op.Id} - \"{op.Message}\"?{Environment.NewLine}{Environment.NewLine}" +
                                   $"Its {op.FileCount} file(s) {where} get back what they had before it: a file it added is deleted. " +
                                   "Only when each is still as it left it - a file changed since stops the rollback before anything changes.",
                "Roll back", "Cancel"))
            return;
        var request = new RollbackRequest(s.Connection.Id, op.Id, s.Root, "", Progress);
        if (await Run($"Roll back #{op.Id} of '{s.Site}'", (sp, reporter, ct) => sp.GetRequiredService<RollbackOperationUseCase>().ExecuteAsync(request, reporter, ct)))
            Toast.Show($"#{op.Id} is rolled back.", ToastKind.Success);
        LoadHistory();
        await ScanAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (OperationFiles.SelectedItem is not OperationFileRow { File: { BeforeHash: { } hash } file } || ChosenOperation is not { } op || Selected is not { } s) return;
        if (!Dialogs.Confirm($"Put {file.Path} back in the project as it was before #{op.Id}?{Environment.NewLine}{Environment.NewLine}" +
                             "The project's version now is kept first. Nothing on the server changes - push it to send it on.",
                "Restore it", "Cancel"))
            return;
        var request = new RestoreVersionRequest(s.Connection.Id, s.Root, file.Path, hash, op.Id);
        if (await Run($"Restore {file.Path} as of #{op.Id}", (sp, reporter, ct) => sp.GetRequiredService<RestoreVersionUseCase>().ExecuteAsync(request, reporter, ct)))
            Toast.Show($"{file.Path} is back as it was before #{op.Id}.", ToastKind.Success);
        LoadHistory();
        await ScanAsync();
    }

    private void OpenReview_Click(object sender, RoutedEventArgs e)
    {
        if (ChosenOperation?.ReviewFolder is { } folder && Directory.Exists(folder)) Shell.Open(folder);
    }
}
