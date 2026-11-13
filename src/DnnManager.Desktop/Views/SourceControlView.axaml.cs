using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.Deployment;
using DnnManager.Desktop.Services;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views;

/// <summary>A status letter's colour, as VS Code's: M modified, A added, D deleted, ! conflict.</summary>
public sealed class LetterBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "M" => Res.Brush("LogWarn"),
        "A" => Res.Brush("SuccessText"),
        "D" => Res.Brush("ErrorText"),
        "!" => Res.Brush("DeployConflict"),
        _ => Res.Brush("TextMuted")
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Source Control - VS Code's, for a project's servers, as the Windows app's (Pages/SourceControlPage). SERVERS lists
/// every project's servers - the projects are the folders in the projects folder, and every project that has a server -
/// and nothing is read from a server until one is chosen: choosing one compares it (Ctrl+click for more than one, each
/// compared in turn, one shown at a time). For the server shown: what differs - staged, to push, to pull, conflicts -
/// each file with its status letter, staged with + and pushed or pulled with a message (Push, its ▾ the rest), after a
/// review of every operation; and the history, each operation rolled back from here, or one file restored. The editor
/// shows the open files as tabs - a change's diff, with a conflict's choices (keep, take, merge) above it - or the chosen
/// operation. The Explorer and Search views beside it list and search the project's files. Everything that touches a
/// server or a project's files runs as an operation (<see cref="OperationRunner"/>): in Output, cancellable, undone
/// when it fails.
/// </summary>
public partial class SourceControlView : UserControl
{
    private readonly IServiceProvider _services = Program.Services;
    private readonly DeployConnections _connections;
    private readonly DeployFiles _files;
    private readonly AppOptions _options;
    private readonly Dictionary<Guid, SyncScan> _scans = [];
    private readonly HashSet<Guid> _autoScanned = [];
    private readonly HashSet<string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ChangeGroup> _collapsed = [];
    private readonly List<ServerRow> _compareQueue = [];
    private readonly ObservableCollection<EditorTab> _tabs = [];
    private SyncScan? _scan;
    private ServerRow? _active;
    private bool _restoringServers, _comparing, _revealing, _followingTabs;
    private CancellationTokenSource? _diffLoad, _prefetch;
    private TransferUpdate? _lastUpdate;
    private bool _updatePending;
    private double _historyHeight = 220;

    // How many files after the chosen one are read ahead.
    private const int PrefetchCount = 4;

    public SourceControlView()
    {
        InitializeComponent();
        _connections = _services.GetRequiredService<DeployConnections>();
        _files = _services.GetRequiredService<DeployFiles>();
        _options = _services.GetRequiredService<IOptions<AppOptions>>().Value;
        Tabs.ItemsSource = _tabs;
        OpenEditors.ItemsSource = _tabs;
        _tabs.CollectionChanged += (_, _) => FollowOpenEditors();
        AddHandler(KeyDownEvent, View_KeyDown, RoutingStrategies.Bubble);
        CommitMessage.AddHandler(KeyDownEvent, CommitMessage_KeyDown, RoutingStrategies.Tunnel);
        Changes.AddHandler(KeyDownEvent, Changes_KeyDown, RoutingStrategies.Tunnel);
        SearchText.AddHandler(KeyDownEvent, SearchText_KeyDown, RoutingStrategies.Tunnel);
        ReplaceText.AddHandler(KeyDownEvent, SearchText_KeyDown, RoutingStrategies.Tunnel);
        Tabs.AddHandler(PointerPressedEvent, Tabs_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tabs.AddHandler(PointerMovedEvent, Tabs_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tabs.AddHandler(PointerReleasedEvent, Tabs_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tabs.AddHandler(DoubleTappedEvent, Tabs_DoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        ChangesMenu.Opening += (_, _) => ChangesMenu_Opening();
        Changes.TemplateApplied += (_, _) => HookChangesScroll();
        Changes.AttachedToVisualTree += (_, _) => HookChangesScroll();
        RevealItem.Header = ExplorerRevealItem.Header = $"Open in {FileManager}";
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = SearchAsync();
        };
        ExplorerTree.ItemsSource = _explorerRows;
        FollowOpenEditors();
        LoadServers(null);
    }

    private static string FileManager => OperatingSystem.IsMacOS() ? "Finder" : OperatingSystem.IsWindows() ? "File Explorer" : "the file manager";

    private OperationRunner Runner => _services.GetRequiredService<OperationRunner>();

    /// <summary>The servers chosen in SERVERS.</summary>
    internal IReadOnlyList<ServerRow> ChosenServers => ServersList.SelectedItems?.OfType<ServerRow>().ToList() ?? [];

    private ServerRow? Selected => _active;

    /// <summary>Shown: the servers chosen and not compared yet are compared - once, by themselves.</summary>
    public void Activated()
    {
        LoadServers(null);
        CompareChosen(ChosenServers);
    }

    // When Settings says to compare a server as it is chosen: those not compared yet this session.
    private void CompareChosen(IEnumerable<ServerRow> servers)
    {
        if (!_connections.CompareOnOpen) return;
        var due = servers.Where(s => !_scans.ContainsKey(s.Connection.Id) && _autoScanned.Add(s.Connection.Id)).ToList();
        if (due.Count > 0) _ = ScanAsync(due);
    }

    // ─── Keys ──────────────────────────────────────────────────────────────

    // F5 compares; Ctrl+Shift+G, E, F show a view of the side bar; Ctrl+W closes the tab; Ctrl+Shift+PageUp / PageDown
    // move it - VS Code's keys. (The window handles them too when the keyboard is elsewhere: see ShowView, CloseActiveTab.)
    private void View_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.F5 && !command)
        {
            _ = ScanAsync();
            e.Handled = true;
        }
        else if (command && shift && e.Key is Key.E or Key.F or Key.G)
        {
            ShowView(e.Key switch { Key.E => SideView.Explorer, Key.F => SideView.Search, _ => SideView.SourceControl });
            e.Handled = true;
        }
        else if (command && !shift && e.Key == Key.W && HasOpenTab)
        {
            CloseActiveTab();
            e.Handled = true;
        }
        else if (command && shift && e.Key is Key.PageUp or Key.PageDown && ActiveTab is { } tab)
        {
            MoveTab(tab, Math.Clamp(_tabs.IndexOf(tab) + (e.Key == Key.PageUp ? -1 : 1), 0, _tabs.Count - 1));
            e.Handled = true;
        }
    }

    // ─── The side bar's views ──────────────────────────────────────────────

    /// <summary>Shows <paramref name="view"/> in the side bar - Ctrl+Shift+G, Ctrl+Shift+E, Ctrl+Shift+F - and puts the keyboard in it.</summary>
    public void ShowView(SideView view)
    {
        ScmTab.IsChecked = view == SideView.SourceControl;
        ExplorerTab.IsChecked = view == SideView.Explorer;
        SearchTab.IsChecked = view == SideView.Search;
        ViewChanged();
        Dispatcher.UIThread.Post(() =>
        {
            switch (view)
            {
                case SideView.Search:
                    SearchText.Focus();
                    SearchText.SelectAll();
                    break;
                case SideView.Explorer:
                    ExplorerTree.Focus();
                    break;
                default:
                    Changes.Focus();
                    break;
            }
        }, DispatcherPriority.Input);
    }

    /// <summary>The view shown in the side bar now.</summary>
    internal SideView CurrentView => ExplorerTab.IsChecked == true ? SideView.Explorer : SearchTab.IsChecked == true ? SideView.Search : SideView.SourceControl;

    // The three icons are one choice: the one clicked is on, the others off.
    private void ViewTab_Click(object? sender, RoutedEventArgs e) =>
        ShowView(sender == ExplorerTab ? SideView.Explorer : sender == SearchTab ? SideView.Search : SideView.SourceControl);

    private void ViewChanged()
    {
        var view = CurrentView;
        ScmView.IsVisible = view == SideView.SourceControl;
        ExplorerView.IsVisible = view == SideView.Explorer;
        SearchView.IsVisible = view == SideView.Search;
        if (view == SideView.Explorer && _explorerStale) _ = LoadExplorerAsync();
    }

    /// <summary>How many files differ, on Source Control's icon - VS Code's badge.</summary>
    private void UpdateBadge()
    {
        var count = _scan?.Changes.Count() ?? 0;
        ChangeBadge.IsVisible = count > 0;
        ChangeBadgeText.Text = count > 999 ? "999+" : count.ToString(CultureInfo.CurrentCulture);
        AutomationProperties.SetName(ScmTab, count > 0 ? $"Source Control, {count} changes" : "Source Control");
    }

    // ─── Servers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The projects: the folders in the projects folder, and every project that has a server (its folder may be
    /// elsewhere - each server's local folder says where).
    /// </summary>
    private IEnumerable<(string Name, string Folder)> Projects()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(_options.BaseDirectory))
                foreach (var folder in Directory.EnumerateDirectories(_options.BaseDirectory))
                    if (Path.GetFileName(folder) is { Length: > 0 } name && !name.StartsWith('.')) names.Add(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var site in _connections.Sites()) names.Add(site);
        return names.Select(n => (n, Path.Combine(_options.BaseDirectory, n)));
    }

    private void LoadServers(Guid? select)
    {
        var rows = new List<ServerRow>();
        foreach (var (name, folder) in Projects())
            foreach (var connection in _connections.For(name))
                rows.Add(new ServerRow(connection, folder, _scans.GetValueOrDefault(connection.Id)));
        // The servers chosen stay chosen - or the one asked for alone. None at first: nothing is read from a server
        // until you choose one.
        var keep = select is { } id ? [id] : ChosenServers.Select(s => s.Connection.Id).ToHashSet();
        var activeId = select ?? _active?.Connection.Id;
        _restoringServers = true;
        try
        {
            ServersList.ItemsSource = rows;
            foreach (var row in rows.Where(r => keep.Contains(r.Connection.Id))) ServersList.SelectedItems?.Add(row);
        }
        finally { _restoringServers = false; }
        var chosen = ChosenServers;
        var active = chosen.FirstOrDefault(r => r.Connection.Id == activeId) ?? chosen.FirstOrDefault();
        var changed = active?.Connection.Id != _active?.Connection.Id;
        _active = active;
        if (changed) ActiveChanged();
        else if (_active is not null && _scans.TryGetValue(_active.Connection.Id, out var scan) && !ReferenceEquals(scan, _scan))
        {
            _scan = scan;
            ShowScan();
        }
        FollowChosen();
        if (select is not null) CompareChosen(chosen);
        var any = rows.Count > 0;
        NoServers.IsVisible = !any;
        EmptyState.IsVisible = !any;
        FileView.IsVisible = any && !OperationView.IsVisible;
        CommitArea.IsEnabled = ScanButton.IsEnabled = any;
        if (!any || _active is null) ShowScan();
    }

    private void Server_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringServers) return;
        var chosen = ChosenServers;
        // The one just chosen is shown; the one shown taken out of the choice, another chosen one is.
        var active = e.AddedItems.OfType<ServerRow>().LastOrDefault()
                     ?? chosen.FirstOrDefault(s => s.Connection.Id == _active?.Connection.Id) ?? chosen.FirstOrDefault();
        var changed = active?.Connection.Id != _active?.Connection.Id;
        _active = active;
        if (changed) ActiveChanged();
        FollowChosen();
        CompareChosen(e.AddedItems.OfType<ServerRow>());
    }

    // Several servers chosen: CHANGES' header names the one shown, a click on it shows another.
    private void FollowChosen()
    {
        ChangesServer.IsVisible = ChosenServers.Count > 1 && _active is not null;
        ChangesServerText.Text = _active?.Title ?? "";
    }

    private void ChangesServer_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var server in ChosenServers)
        {
            var item = new MenuItem
            {
                Header = server.Title, InputGesture = null, ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = server.Connection.Id == _active?.Connection.Id
            };
            if (server.Counts.Length > 0) item.Header = $"{server.Title}    {server.Counts}";
            item.Click += (_, _) => ShowServer(server);
            menu.Items.Add(item);
        }
        menu.Open(ChangesServer);
    }

    /// <summary>Shows <paramref name="server"/>'s changes, history and files - one of the servers chosen; the others stay chosen.</summary>
    internal void ShowServer(ServerRow server)
    {
        if (server.Connection.Id == _active?.Connection.Id) return;
        _active = ChosenServers.FirstOrDefault(s => s.Connection.Id == server.Connection.Id) ?? server;
        ActiveChanged();
        FollowChosen();
    }

    /// <summary>Another server shown: its comparison, history and files - the tabs of the one before closed.</summary>
    private void ActiveChanged()
    {
        _staged.Clear();
        _scan = _active is { } s ? _scans.GetValueOrDefault(s.Connection.Id) : null;
        _tabs.Clear();
        ShowFile(null);
        ShowScan();
        LoadHistory();
        ServerChangedForViews();
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var projects = Projects().Select(p => p.Name).ToList();
        if (await ServerDialog.ShowAsync(_connections, _options.BaseDirectory, projects, null) is { } added) LoadServers(added.Id);
    }

    // The server a menu acts on: the one shown.
    private ServerRow? MenuServer(object? sender) => (sender as Control)?.Tag as ServerRow ?? _active;

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuServer(sender) is not { } s) return;
        var current = _connections.Find(s.Connection.Id) ?? s.Connection;
        if (await ServerDialog.ShowAsync(_connections, _options.BaseDirectory, [s.Site], current) is not { } edited) return;
        // Other rules or another folder: what was compared isn't what would be pushed.
        _scans.Remove(edited.Id);
        if (_active?.Connection.Id == edited.Id) _scan = null;
        LoadServers(edited.Id);
        ShowScan();
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuServer(sender) is not { } s || _connections.Find(s.Connection.Id) is not { } connection || !await EnsurePasswordAsync(connection)) return;
        Say($"Testing {connection.Name}…");
        var check = await Task.Run(() => _connections.TestAsync(connection, _connections.Secrets(connection.Id), CancellationToken.None));
        _connections.RecordCheck(connection.Id, check);
        LoadServers(null);
        Say(check.NeedsTrust ? $"{check.Message} Edit the connection to check and trust it." : check.Message);
        if (!check.Ok)
            await Dialogs.MessageAsync(check.NeedsTrust ? $"{check.Message} Edit the connection and test it there to check and trust its identity." : check.Message,
                "Couldn't connect");
    }

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuServer(sender) is not { } s) return;
        if (!await Dialogs.ConfirmAsync($"Remove the connection '{s.Name}' of '{s.Site}' ({s.Connection.Host})?{Environment.NewLine}{Environment.NewLine}" +
                                        "Its saved password or key, its baseline and its history of pushes and pulls are deleted. No file is deleted - " +
                                        "not in the project, not on the server.", "Remove connection", "Keep it", danger: true))
            return;
        _connections.Delete(s.Connection.Id);
        _scans.Remove(s.Connection.Id);
        if (_active?.Connection.Id == s.Connection.Id)
        {
            _active = null;
            _scan = null;
        }
        LoadServers(null);
        ActiveChanged();
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuServer(sender) is { } s && Directory.Exists(s.Root)) await OpenFolderAsync(s.Root);
    }

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var scanned = _scan is not null;
        menu.Items.Add(Item("Stage all to push", () => StageAll(ChangeGroup.Outgoing), scanned));
        menu.Items.Add(Item("Stage all to pull", () => StageAll(ChangeGroup.Incoming), scanned));
        menu.Items.Add(Item("Unstage all", () => { _staged.Clear(); Fill(); }, _staged.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Edit connection…", () => Edit_Click(null, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(Item("Test connection", () => Test_Click(null, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(Item("Remove connection…", () => Remove_Click(null, new RoutedEventArgs()), Selected is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Source Control settings…", () => Main?.Show("Settings")));
        menu.Open(MoreButton);
    }

    private static MenuItem Item(string header, Action run, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    // ─── Comparing ────────────────────────────────────────────────────────

    private void Scan_Click(object? sender, RoutedEventArgs e) => _ = ScanAsync();

    /// <summary>Compares every server chosen (F5, ⟳) - the one shown when none is.</summary>
    private Task ScanAsync() => ScanAsync(ChosenServers.Count > 0 ? ChosenServers : _active is { } s ? [s] : []);

    /// <summary>Compares <paramref name="servers"/>, one after the other - asked while one runs, after it.</summary>
    private async Task ScanAsync(IReadOnlyList<ServerRow> servers)
    {
        foreach (var server in servers)
            if (!_compareQueue.Any(q => q.Connection.Id == server.Connection.Id)) _compareQueue.Add(server);
        if (_comparing) return;
        _comparing = true;
        try
        {
            while (_compareQueue.Count > 0)
            {
                var next = _compareQueue[0];
                _compareQueue.RemoveAt(0);
                // Refused - another operation (a push…) runs, and said so: the rest wait for F5.
                if (await CompareAsync(next)) continue;
                _compareQueue.Clear();
                break;
            }
        }
        finally { _comparing = false; }
    }

    // "Ask for password" and none given this session: asked now - false when cancelled.
    private async Task<bool> EnsurePasswordAsync(DeployConnection stale)
    {
        var connection = _connections.Find(stale.Id) ?? stale;
        if (!_connections.NeedsPassword(connection)) return true;
        if (await Dialogs.PasswordAsync($"Password for {connection.UserName} on {connection.Host} ({connection.Name}) - kept until DNN Manager closes:",
                "Connect") is not { } password)
            return false;
        _connections.RememberForSession(connection.Id, password);
        return true;
    }

    // One server compared - false when it couldn't start (another operation runs).
    private async Task<bool> CompareAsync(ServerRow server)
    {
        if (_connections.Find(server.Connection.Id) is not { } connection) return true;
        if (!await EnsurePasswordAsync(connection)) return true;
        if (!Directory.Exists(server.Root))
        {
            await Dialogs.MessageAsync($"The project's folder {server.Root} isn't there. Edit {connection.Name} and choose its local folder.");
            return true;
        }
        SyncScan? result = null;
        ShowProgress(null);
        var started = false;
        try
        {
            started = await Runner.RunAsync($"Compare '{connection.Site}' with {connection.Name} ({connection.Environment})", async (sp, reporter, ct) =>
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
        finally { HideProgress(); }
        if (result is not null) _scans[connection.Id] = result;
        if (!started && result is null) Main?.ShowOutput(true);
        LoadServers(null);
        if (_active?.Connection.Id != connection.Id || result is null) return started || result is not null;
        _scan = result;
        // What is staged stays staged while it still has the same way to go.
        _staged.RemoveWhere(p => result.Find(p) is not { Direction: SyncDirection.Push or SyncDirection.Pull });
        ShowScan();
        return true;
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
            StickyHeader.IsVisible = false;
            ChangesMessage.Text = _active is not null ? "Not compared yet - Compare (F5) to see what differs."
                : ServersList.ItemCount > 0 ? "Choose a server above to compare it with its project - Ctrl+click (⌘+click) to choose more than one." : "";
            ChangesMessage.IsVisible = ChangesMessage.Text.Length > 0;
            UpdateButtons();
            UpdateBadge();
            ExplorerFollowScan();
            return;
        }
        UpdateBadge();
        ScanSummary.Text = $"compared {_scan.ScannedUtc.ToLocalTime():t}" + (_scan.Warnings.Count > 0 ? $" · {_scan.Warnings.Count} left out" : "");
        ToolTip.SetTip(ScanSummary, _scan.Warnings.Count > 0 ? string.Join(Environment.NewLine, _scan.Warnings.Take(30)) : null);
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
            if (!collapsed) rows.AddRange(items.Select(i => new ChangeRow { Group = group, Item = i, IsStaged = staged }));
        }
        Group(ChangeGroup.Staged, "Staged Changes", [.. changes.Where(i => _staged.Contains(i.Path))], true);
        Group(ChangeGroup.Outgoing, "Changes - to push", [.. changes.Where(i => i.Direction == SyncDirection.Push && !_staged.Contains(i.Path))], false);
        Group(ChangeGroup.Incoming, "Incoming - to pull", [.. changes.Where(i => i.Direction == SyncDirection.Pull && !_staged.Contains(i.Path))], false);
        Group(ChangeGroup.Conflicts, "Conflicts - changed in both", [.. changes.Where(i => i.Direction == SyncDirection.Conflict)], false);
        _revealing = true;
        try
        {
            Changes.ItemsSource = rows;
            // The row chosen again in the new list - its tab is open already.
            if (chosen is not null) Changes.SelectedItem = rows.FirstOrDefault(r => r.IsFile && r.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        }
        finally { _revealing = false; }
        FollowTabs();
        Dispatcher.UIThread.Post(UpdateStickyHeader, DispatcherPriority.Loaded);
        ChangesMessage.Text = changes.Count == 0 ? "The project and the server are the same - nothing to push or pull." : "";
        ChangesMessage.IsVisible = changes.Count == 0;
        UpdateButtons();
        ExplorerFollowScan();
    }

    // ─── Staging ───────────────────────────────────────────────────────────

    private void Stage_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ChangeRow { Item: { } item }) Stage([item], true);
        e.Handled = true;
    }

    private void Unstage_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ChangeRow { Item: { } item }) Stage([item], false);
        e.Handled = true;
    }

    private void StageGroup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ChangeRow row) StageAll(row.Group);
        e.Handled = true;
    }

    private void UnstageGroup_Click(object? sender, RoutedEventArgs e)
    {
        _staged.Clear();
        Fill();
        e.Handled = true;
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

    private void Changes_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Changes.SelectedItem is not ChangeRow row) return;
        // Space stages or unstages, as VS Code's + and -; on a header, opens or closes the group. Enter keeps the tab open.
        if (e.Key == Key.Space)
        {
            if (row.IsHeader) Toggle(row.Group);
            else if (row.Item is { } item) Stage([item], !row.IsStaged);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && row.Item is { } file)
        {
            OpenTab(file, keep: true);
            e.Handled = true;
        }
    }

    /// <summary>A click on a group's header - in the list, or the one kept at its top - opens or closes the group, as in VS Code.</summary>
    private void Header_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChangeRow { IsHeader: true } row) return;
        e.Handled = true;
        Toggle(row.Group);
    }

    private void StickyHeader_Tapped(object? sender, TappedEventArgs e)
    {
        if (StickyHeader.Tag is ChangeGroup group) Toggle(group);
    }

    private void Toggle(ChangeGroup group)
    {
        if (!_collapsed.Remove(group)) _collapsed.Add(group);
        Fill();
        // The group's header where it can be seen - closed from the top while scrolled down, it is no longer under it.
        if (Changes.ItemsSource is IReadOnlyList<ChangeRow> rows && rows.FirstOrDefault(r => r.IsHeader && r.Group == group) is { } header)
        {
            _revealing = true;
            try { Changes.SelectedItem = header; }
            finally { _revealing = false; }
            Changes.ScrollIntoView(header);
        }
        Dispatcher.UIThread.Post(UpdateStickyHeader, DispatcherPriority.Loaded);
    }

    // ─── The header of the group scrolled through, kept at the list's top ──

    private ScrollViewer? _changesScroll;

    private void HookChangesScroll()
    {
        var scroll = Changes.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null || ReferenceEquals(scroll, _changesScroll)) return;
        _changesScroll = scroll;
        scroll.ScrollChanged += (_, _) => UpdateStickyHeader();
    }

    /// <summary>
    /// Scrolled down into a group, its header stays at the top of the list - VS Code's sticky scroll - so it can be closed
    /// without scrolling back up. Hidden while the group's own header is there to be seen.
    /// </summary>
    private void UpdateStickyHeader()
    {
        HookChangesScroll();
        var row = StickyRow();
        StickyHeader.IsVisible = row is not null;
        if (row is null) return;
        StickyHeader.Tag = row.Group;
        StickyTitle.Text = row.Title;
        StickyCount.Text = row.Count.ToString(CultureInfo.CurrentCulture);
    }

    private ChangeRow? StickyRow()
    {
        if (Changes.ItemsSource is not IReadOnlyList<ChangeRow> rows || rows.Count == 0 || _changesScroll is not { Offset.Y: > 0.5 }) return null;
        // The row at the list's top edge.
        if ((Changes.InputHitTest(new Point(12, 2)) as Visual)?.FindAncestorOfType<ListBoxItem>(true) is not { DataContext: ChangeRow first } item) return null;
        // Its own header, all of it in sight: no need for another.
        if (first.IsHeader && item.TranslatePoint(new Point(), Changes) is { Y: >= 0 }) return null;
        var at = -1;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], first)) at = i;
        for (var i = at; i >= 0; i--)
            if (rows[i].IsHeader) return rows[i];
        return null;
    }

    // ─── The changes' menu ─────────────────────────────────────────────────

    // The folder of the row the menu was opened on: stage or unstage everything in it.
    private string? MenuFolder => Changes.SelectedItem is ChangeRow { IsHeader: false } row && row.Folder.Length > 0 ? row.Folder : null;

    private void ChangesMenu_Opening()
    {
        var folder = MenuFolder;
        StageFolderItem.Header = folder is null ? "Stage everything in the folder" : $"Stage everything in {folder}/";
        UnstageFolderItem.Header = folder is null ? "Unstage everything in the folder" : $"Unstage everything in {folder}/";
        StageFolderItem.IsEnabled = UnstageFolderItem.IsEnabled = folder is not null;
    }

    private IEnumerable<SyncItem> InFolder(string folder) =>
        _scan?.Changes.Where(i => i.Path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) ?? [];

    private void StageFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuFolder is { } folder) Stage(InFolder(folder).Where(i => !i.Sensitive), true);
    }

    private void UnstageFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (MenuFolder is { } folder) Stage(InFolder(folder), false);
    }

    private async void CopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { IsHeader: false } row) await CopyAsync(row.Path);
    }

    private async void Reveal_Click(object? sender, RoutedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { IsHeader: false } row && Selected is { } s && DeployPath.Local(s.Root, row.Path) is { } file)
            await OpenFolderAsync(File.Exists(file) ? Path.GetDirectoryName(file)! : s.Root);
    }

    // ─── Push and Pull: VS Code's Commit button and its ▾ ──────────────────

    private IReadOnlyList<SyncItem> StagedItems(SyncDirection direction) =>
        _scan is null ? [] : [.. _staged.Select(p => _scan.Find(p)).OfType<SyncItem>().Where(i => i.Direction == direction)];

    /// <summary>Push when files to push are staged, Pull when only files to pull are - VS Code's Commit button.</summary>
    private void UpdateButtons()
    {
        var push = StagedItems(SyncDirection.Push).Count;
        var pull = StagedItems(SyncDirection.Pull).Count;
        var pulling = push == 0 && pull > 0;
        PrimaryLabel.Text = pulling ? $"Pull {pull}" : push > 0 ? $"Push {push}" : "Push";
        PrimaryGlyph.Text = pulling ? Icon.Download : Icon.Upload;
        PrimaryButton.IsEnabled = push + pull > 0;
        AutomationProperties.SetName(PrimaryButton, PrimaryLabel.Text);
        ToolTip.SetTip(PrimaryButton, pulling
            ? "Pull the staged files - from the server into the project, after a review of each"
            : "Push the staged files - from the project to the server, after a review of each (Ctrl+Enter)");
        CommitMessage.PlaceholderText = pulling ? "Message (Ctrl+Enter to pull)" : "Message (Ctrl+Enter to push)";
    }

    private void Primary_Click(object? sender, RoutedEventArgs e)
    {
        if (StagedItems(SyncDirection.Push).Count > 0) _ = PushAsync();
        else if (StagedItems(SyncDirection.Pull).Count > 0) _ = PullAsync(false);
    }

    private void CommitMessage_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !(e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta)) return;
        e.Handled = true;
        if (PrimaryButton.IsEnabled) Primary_Click(this, new RoutedEventArgs());
    }

    private void Actions_Click(object? sender, RoutedEventArgs e)
    {
        var push = StagedItems(SyncDirection.Push).Count;
        var pull = StagedItems(SyncDirection.Pull).Count;
        var menu = new ContextMenu();
        menu.Items.Add(Item(push > 0 ? $"Push {push} staged" : "Push", () => _ = PushAsync(), push > 0));
        menu.Items.Add(Item(pull > 0 ? $"Pull {pull} staged" : "Pull", () => _ = PullAsync(false), pull > 0));
        menu.Items.Add(Item(pull > 0 ? $"Pull {pull} staged into a review folder" : "Pull into a review folder", () => _ = PullAsync(true), pull > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Compare again", () => _ = ScanAsync(), Selected is not null));
        menu.Open(ActionsButton);
    }

    // ─── Tabs ──────────────────────────────────────────────────────────────

    private EditorTab? ActiveTab => Tabs.SelectedItem as EditorTab;

    /// <summary>A file is open in a tab - Ctrl+W closes it.</summary>
    public bool HasOpenTab => ActiveTab is not null;

    /// <summary>Ctrl+W: the chosen tab closed.</summary>
    public void CloseActiveTab()
    {
        if (ActiveTab is { } tab) CloseTab(tab);
    }

    /// <summary>The open tabs, in order - for a test.</summary>
    internal IReadOnlyList<EditorTab> OpenTabs => _tabs;

    private void Change_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_revealing && Changes.SelectedItem is ChangeRow { Item: { } item }) OpenTab(item, keep: false);
    }

    /// <summary>A double-click on a file keeps its tab open.</summary>
    private void Changes_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Changes.SelectedItem is ChangeRow { Item: { } item }) OpenTab(item, keep: true);
    }

    /// <summary>
    /// Opens <paramref name="item"/> as VS Code does: a click in the preview tab - the one open in italics, replaced by the
    /// next click's file - and a double-click (<paramref name="keep"/>) in a tab that stays. Open already, its tab is chosen.
    /// </summary>
    internal void OpenTab(SyncItem item, bool keep)
    {
        var tab = _tabs.FirstOrDefault(t => !t.IsFile && DeployPath.Comparer.Equals(t.Path, item.Path));
        if (tab is null)
        {
            if (!keep && _tabs.FirstOrDefault(t => t.IsPreview) is { } preview)
            {
                preview.Item = item;
                tab = preview;
            }
            else
            {
                tab = new EditorTab(item, preview: !keep);
                // Next to the chosen tab, as VS Code opens one.
                var at = ActiveTab is { } active ? _tabs.IndexOf(active) + 1 : _tabs.Count;
                _tabs.Insert(at, tab);
            }
        }
        else if (keep) tab.IsPreview = false;
        Activate(tab);
    }

    /// <summary>
    /// Opens a project's file as it is - from the Explorer or a search result (<paramref name="at"/>: the line and match
    /// shown) - in the preview tab, or one that stays (<paramref name="keep"/>), as <see cref="OpenTab"/> does a change.
    /// </summary>
    internal void OpenFile(string path, bool keep, (int Line, int Start, int Length)? at = null)
    {
        var tab = _tabs.FirstOrDefault(t => t.File is { } file && DeployPath.Comparer.Equals(file, path));
        if (tab is null)
        {
            if (!keep && _tabs.FirstOrDefault(t => t.IsPreview) is { } preview)
            {
                preview.File = path;
                tab = preview;
            }
            else
            {
                tab = new EditorTab(path, preview: !keep);
                var index = ActiveTab is { } active ? _tabs.IndexOf(active) + 1 : _tabs.Count;
                _tabs.Insert(index, tab);
            }
        }
        else if (keep) tab.IsPreview = false;
        tab.RevealAt = at;
        Activate(tab);
    }

    /// <summary>Shows <paramref name="tab"/>'s file - also when it was the chosen tab already (the preview given another file).</summary>
    private void Activate(EditorTab tab)
    {
        if (ReferenceEquals(Tabs.SelectedItem, tab)) _ = ShowTabAsync(tab);
        else Tabs.SelectedItem = tab;
        Tabs.ScrollIntoView(tab);
    }

    private void Tab_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ActiveTab is { } tab)
        {
            _ = ShowTabAsync(tab);
            Reveal(tab.Item);
        }
        else if (_tabs.Count == 0) ShowFile(null);
        FollowOpenEditors();
    }

    /// <summary>The tab's row chosen in the list, as VS Code follows the editor - without opening it again.</summary>
    private void Reveal(SyncItem? item)
    {
        if (Changes.ItemsSource is not IReadOnlyList<ChangeRow> rows) return;
        var row = item is null ? null : rows.FirstOrDefault(r => !r.IsHeader && DeployPath.Comparer.Equals(r.Path, item.Path));
        _revealing = true;
        try { Changes.SelectedItem = row; }
        finally { _revealing = false; }
    }

    // A middle-click closes a tab (here or in Open Editors); a click on the chosen one brings its file back from an operation.
    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EditorTab tab) return;
        var point = e.GetCurrentPoint(sender as Control);
        if (point.Properties.IsMiddleButtonPressed)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed && ReferenceEquals(ActiveTab, tab) && !FileView.IsVisible)
            _ = ShowTabAsync(tab);
    }

    private void Tabs_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext is EditorTab tab) tab.IsPreview = false;
    }

    // ─── Moving a tab: dragged to another place, as in VS Code ─────────────
    // The tabs stay where they are while one is dragged; a line shows where it will go, and it moves there when it is
    // dropped - nothing jumps under the pointer.

    private EditorTab? _pressedTab, _draggedTab;
    private Point _tabDragFrom;

    private void Tabs_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Tabs);
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true);
        if (point.Properties.IsMiddleButtonPressed && item?.DataContext is EditorTab middle)
        {
            CloseTab(middle);
            e.Handled = true;
            return;
        }
        // Not from its × - that closes it.
        _pressedTab = point.Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<Button>(true) is null
            ? item?.DataContext as EditorTab : null;
        _tabDragFrom = point.Position;
        if (_pressedTab is not null && ReferenceEquals(ActiveTab, _pressedTab) && !FileView.IsVisible) _ = ShowTabAsync(_pressedTab);
    }

    private void Tabs_PointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(Tabs);
        if (_draggedTab is null)
        {
            if (_pressedTab is not { } tab || !point.Properties.IsLeftButtonPressed || _tabs.Count < 2) return;
            if (Math.Abs(point.Position.X - _tabDragFrom.X) < 4 && Math.Abs(point.Position.Y - _tabDragFrom.Y) < 4) return;
            _draggedTab = tab;
            _pressedTab = null;
            e.Pointer.Capture(Tabs);
            if (Tabs.ContainerFromItem(tab) is { } container) container.Opacity = 0.5;
        }
        var bounds = TabBounds();
        var gap = DropGap(point.Position.X, bounds);
        var from = _tabs.IndexOf(_draggedTab);
        // Where it is already: no line - dropped there, it stays.
        if (gap == from || gap == from + 1 || bounds.Count != _tabs.Count)
        {
            TabDropLine.IsVisible = false;
            return;
        }
        var x = gap < bounds.Count ? bounds[gap].Left : bounds[^1].Left + bounds[^1].Width;
        Canvas.SetLeft(TabDropLine, Math.Clamp(x - 1, 0, Math.Max(0, Tabs.Bounds.Width - 2)));
        TabDropLine.Height = Tabs.Bounds.Height;
        TabDropLine.IsVisible = true;
    }

    private void Tabs_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedTab = null;
        if (_draggedTab is not { } dragged) return;
        _draggedTab = null;
        e.Pointer.Capture(null);
        TabDropLine.IsVisible = false;
        if (Tabs.ContainerFromItem(dragged) is { } container) container.Opacity = 1;
        var bounds = TabBounds();
        if (bounds.Count != _tabs.Count) return;
        MoveTab(dragged, TargetIndex(_tabs.IndexOf(dragged), DropGap(e.GetPosition(Tabs).X, bounds)));
        e.Handled = true;
    }

    // Each tab's left edge and width in the strip, in order - as it is scrolled.
    private List<(double Left, double Width)> TabBounds()
    {
        var bounds = new List<(double, double)>();
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is not { } item || item.TranslatePoint(new Point(0, 0), Tabs) is not { } at) break;
            bounds.Add((at.X, item.Bounds.Width));
        }
        return bounds;
    }

    /// <summary>
    /// The gap a tab dropped at <paramref name="x"/> goes into: 0 before the first tab, n after the last - before the first
    /// tab whose middle is right of the pointer, as VS Code drops one.
    /// </summary>
    internal static int DropGap(double x, IReadOnlyList<(double Left, double Width)> tabs)
    {
        for (var i = 0; i < tabs.Count; i++)
            if (x < tabs[i].Left + tabs[i].Width / 2) return i;
        return tabs.Count;
    }

    /// <summary>The index a tab at <paramref name="from"/> takes, dropped into <paramref name="gap"/> - the gaps after it are one fewer once it is out.</summary>
    internal static int TargetIndex(int from, int gap) => gap > from ? gap - 1 : gap;

    /// <summary>Moves <paramref name="tab"/> to <paramref name="to"/> - still the chosen one if it was, its file still shown.</summary>
    internal void MoveTab(EditorTab tab, int to)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0 || to < 0 || to >= _tabs.Count || from == to) return;
        var wasActive = ReferenceEquals(ActiveTab, tab);
        _revealingTab = true;
        try
        {
            _tabs.Move(from, to);
            if (wasActive && !ReferenceEquals(Tabs.SelectedItem, tab)) Tabs.SelectedItem = tab;
        }
        finally { _revealingTab = false; }
        Tabs.ScrollIntoView(tab);
    }

    // A tab moved: still chosen, its file not read again.
    private bool _revealingTab;

    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is EditorTab tab) CloseTab(tab);
        e.Handled = true;
    }

    /// <summary>Closes <paramref name="tab"/>; the chosen one closed, the tab beside it is chosen - as VS Code does.</summary>
    private void CloseTab(EditorTab tab)
    {
        var at = _tabs.IndexOf(tab);
        if (at < 0) return;
        var wasActive = ReferenceEquals(ActiveTab, tab);
        _tabs.RemoveAt(at);
        if (_tabs.Count == 0)
        {
            ShowFile(null);
            Reveal(null);
            return;
        }
        if (wasActive) Tabs.SelectedItem = _tabs[Math.Min(at, _tabs.Count - 1)];
    }

    /// <summary>
    /// A new comparison (or one staged): each tab's file as it compares now - a file no longer different closes its tab; the
    /// chosen one shows again when its file changed.
    /// </summary>
    private void FollowTabs()
    {
        if (_scan is null) return;
        foreach (var tab in _tabs.Where(t => !t.IsFile).ToList())
        {
            var now = _scan.Changes.FirstOrDefault(i => DeployPath.Comparer.Equals(i.Path, tab.Path));
            if (now is null) CloseTab(tab);
            else if (!ReferenceEquals(now, tab.Item))
            {
                tab.Item = now;
                if (ReferenceEquals(ActiveTab, tab)) _ = ShowFileAsync(now);
            }
        }
        if (ActiveTab is { } active) Dispatcher.UIThread.Post(() => Reveal(active.Item), DispatcherPriority.Loaded);
    }

    // ─── Open editors ──────────────────────────────────────────────────────

    // The open editors follow the tabs: the chosen one chosen here too.
    private void FollowOpenEditors()
    {
        OpenEditorsMessage.IsVisible = _tabs.Count == 0;
        _followingTabs = true;
        try { OpenEditors.SelectedItem = ActiveTab; }
        finally { _followingTabs = false; }
    }

    private void OpenEditors_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_followingTabs || OpenEditors.SelectedItem is not EditorTab tab) return;
        Activate(tab);
    }

    // ─── The editor ────────────────────────────────────────────────────────

    /// <summary>The editor shows the file view - nothing chosen when <paramref name="item"/> is null.</summary>
    private void ShowFile(SyncItem? item)
    {
        OperationView.IsVisible = false;
        FileView.IsVisible = ServersList.ItemCount > 0;
        _restoringHistory = true;
        try { HistoryList.SelectedItem = null; }
        finally { _restoringHistory = false; }
        TabStrip.IsVisible = _tabs.Count > 0;
        Breadcrumb.IsVisible = item is not null;
        if (item is null)
        {
            _diffLoad?.Cancel();
            Breadcrumb.Text = "";
            ConflictBar.IsVisible = false;
            Diff.ShowMessage(Selected is null ? "Choose a server, then a file, to see what changed." : "Choose a file to see what changed.");
            return;
        }
        var folder = DeployPath.Folder(item.Path);
        Breadcrumb.Text = (folder.Length > 0 ? folder.Replace("/", " › ") + " › " : "") + DeployPath.Name(item.Path) + " · " + item.Describe().ToLowerInvariant();
        ConflictBar.IsVisible = item.Direction == SyncDirection.Conflict;
        ConflictText.Text = $"{item.Path}: {item.Describe().ToLowerInvariant()} since they last agreed. Nothing is pushed or pulled until you choose which " +
                            "version stays - the server only changes when you push.";
        MergeButton.IsEnabled = item is { Local: not null, Remote: not null };
    }

    private Task ShowTabAsync(EditorTab tab) => _revealingTab ? Task.CompletedTask : tab.Item is { } item ? ShowFileAsync(item) : ShowProjectFileAsync(tab);

    /// <summary>A project's file as it is, read from the project's folder - at the line a search result opened it at.</summary>
    private async Task ShowProjectFileAsync(EditorTab tab)
    {
        _diffLoad?.Cancel();
        _prefetch?.Cancel();
        if (Selected is not { } server || tab.File is not { } path) return;
        OperationView.IsVisible = false;
        FileView.IsVisible = true;
        TabStrip.IsVisible = true;
        ConflictBar.IsVisible = false;
        Breadcrumb.IsVisible = true;
        var folder = DeployPath.Folder(path);
        Breadcrumb.Text = (folder.Length > 0 ? folder.Replace("/", " › ") + " › " : "") + DeployPath.Name(path) + " · in the project";
        var load = _diffLoad = new CancellationTokenSource();
        Diff.ShowMessage($"Reading {path}…");
        try
        {
            var bytes = await Task.Run(() =>
            {
                var full = ProjectFiles.Full(server.Root, path);
                return new FileInfo(full).Length > TextContent.MaxBytes ? null : File.ReadAllBytes(full);
            }, load.Token);
            if (load.IsCancellationRequested) return;
            if (bytes is null)
            {
                Diff.ShowMessage($"{path} is larger than {TextContent.MaxBytes / (1024 * 1024)} MB - too big to show.");
                return;
            }
            await Diff.ShowFileAsync(path, bytes, "In the project");
            if (load.IsCancellationRequested || tab.RevealAt is not { } at) return;
            tab.RevealAt = null;
            Diff.Reveal(at.Line, at.Start, at.Length);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!load.IsCancellationRequested) Diff.ShowMessage($"Couldn't read {path}: {ex.Message}");
        }
    }

    private async Task ShowFileAsync(SyncItem item)
    {
        _diffLoad?.Cancel();
        _prefetch?.Cancel();
        if (_scan is not { } scan) return;
        ShowFile(item);
        if (item.Remote is not null && !await EnsurePasswordAsync(scan.Connection))
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
            // Old on the left, new on the right: what the push or pull changes.
            if (item.Direction == SyncDirection.Pull) await Diff.ShowDiffAsync(item.Path, versions.Local, versions.Remote, project, server);
            else await Diff.ShowDiffAsync(item.Path, versions.Remote, versions.Local, server, project);
            if (!load.IsCancellationRequested && (item.Remote is not null || !_connections.NeedsPassword(_connections.Find(scan.Connection.Id) ?? scan.Connection)))
                Prefetch(scan, item);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is RemoteException or IOException or UnauthorizedAccessException)
        {
            if (!load.IsCancellationRequested) Diff.ShowMessage($"Couldn't read {item.Path}: {Explain(ex)}");
        }
    }

    /// <summary>
    /// The server's versions of the files after <paramref name="item"/> in the list, read ahead on the connection kept for
    /// viewing - going down the list, each shows at once, as in VS Code.
    /// </summary>
    private void Prefetch(SyncScan scan, SyncItem item)
    {
        if (Changes.ItemsSource is not IReadOnlyList<ChangeRow> rows) return;
        var at = -1;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i].Item, item)) at = i;
        if (at < 0) return;
        var next = rows.Skip(at + 1).Select(r => r.Item).OfType<SyncItem>().Where(i => i.Remote is not null).Take(PrefetchCount).ToList();
        if (next.Count == 0) return;
        _prefetch?.Cancel();
        var prefetch = _prefetch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await _files.PrefetchAsync(scan, next, prefetch.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or RemoteException or IOException)
            {
                // Read ahead only: the file is read when it is chosen.
            }
        });
    }

    // ─── Conflicts ─────────────────────────────────────────────────────────

    private SyncItem? ChosenConflict => ActiveTab is { Item: { Direction: SyncDirection.Conflict } item } ? item : null;

    private void KeepLocal_Click(object? sender, RoutedEventArgs e)
    {
        if (ChosenConflict is { } item) _ = ResolveAsync(item, ConflictChoice.KeepLocal, null, null);
    }

    private async void TakeRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (ChosenConflict is not { } item) return;
        var what = item.Remote is null
            ? $"The server deleted {item.Path}: it is deleted from the project too."
            : $"The server's {item.Path} replaces the project's.";
        if (!await Dialogs.ConfirmAsync($"{what}{Environment.NewLine}{Environment.NewLine}The project's version is kept first - the history rolls it back.",
                item.Remote is null ? "Delete it here too" : "Take the server's", "Cancel"))
            return;
        await ResolveAsync(item, ConflictChoice.TakeRemote, null, null);
    }

    private async void Merge_Click(object? sender, RoutedEventArgs e)
    {
        if (ChosenConflict is not { } item || _scan is not { } scan) return;
        if (item.Remote is not null && !await EnsurePasswordAsync(scan.Connection)) return;
        FileVersions versions;
        try
        {
            versions = await Task.Run(() => _files.VersionsAsync(scan, item, CancellationToken.None));
        }
        catch (Exception ex) when (ex is RemoteException or IOException)
        {
            await Dialogs.MessageAsync($"Couldn't read {item.Path}: {Explain(ex)}");
            return;
        }
        if (versions.Local is null || versions.Remote is null || versions.IsBinary || versions.TooLarge)
        {
            await Dialogs.MessageAsync($"{item.Path} can't be merged as text - keep the project's version or take the server's.");
            return;
        }
        if (await MergeDialog.ShowAsync(item.Path, versions.Base, versions.Local, versions.Remote) is { } merged)
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
        if (await DeployPreviewDialog.ShowAsync(true, connection, items, CommitMessage.Text ?? "") is not { } consent) return;
        var request = new PushRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Push"), consent.AllowSensitive, Progress);
        if (await Run($"Push {items.Count} file(s) of '{connection.Site}' to {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PushChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            Say($"Pushed {items.Count} file(s) to {connection.Name}.");
            AfterCommit(items);
        }
        await ScanAsync();
    }

    private async Task PullAsync(bool intoReview)
    {
        if (_scan is not { } scan || _connections.Find(scan.Connection.Id) is not { } connection) return;
        var items = StagedItems(SyncDirection.Pull);
        if (items.Count == 0) return;
        if (await DeployPreviewDialog.ShowAsync(false, connection, items, CommitMessage.Text ?? "", intoReview) is not { } consent) return;
        var review = consent.IntoReviewFolder
            ? Path.Combine(_services.GetRequiredService<AppDataPaths>().DeploymentsDirectory, $"{connection.Site}_review_{DateTime.Now:yyyyMMdd_HHmmss}")
            : null;
        var request = new PullRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Pull"), consent.AllowSensitive, review, Progress);
        if (await Run($"Pull {items.Count} file(s) of '{connection.Site}' from {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PullChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            Say(review is not null ? $"The server's {items.Count} file(s) are in {review} - the project didn't change." : $"Pulled {items.Count} file(s) from {connection.Name}.");
            AfterCommit(items);
            if (review is not null && await Dialogs.ConfirmAsync($"The server's {items.Count} file(s) are in a review folder - the project didn't change.",
                    "Open folder", "Close"))
                await OpenFolderAsync(review);
        }
        await ScanAsync();
    }

    private string Message(IReadOnlyList<SyncItem> items, string verb) =>
        (CommitMessage.Text ?? "").Trim() is { Length: > 0 } typed ? typed
            : items.Count == 1 ? $"{verb} {items[0].Path}" : $"{verb} {items.Count} files";

    private void AfterCommit(IReadOnlyList<SyncItem> done)
    {
        foreach (var item in done) _staged.Remove(item.Path);
        CommitMessage.Text = "";
        LoadHistory();
    }

    /// <summary>Runs a use case as an operation - its progress shown here, a failure in Output.</summary>
    private async Task<bool> Run(string title, Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result<DeployOperation>>> work)
    {
        if (_active is { } server && !await EnsurePasswordAsync(server.Connection)) return false;
        ShowProgress(title);
        try
        {
            var ok = await Runner.RunAsync(title, async (sp, reporter, ct) =>
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
            if (!ok) Main?.ShowOutput(true);
            return ok;
        }
        finally { HideProgress(); }
    }

    private void ShowProgress(string? text)
    {
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Opacity = 1;
        ProgressText.Text = text ?? "";
        ProgressText.IsVisible = text is not null;
    }

    private void HideProgress()
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Opacity = 0;
        ProgressText.IsVisible = false;
    }

    /// <summary>A transfer's progress, from the thread pool: the latest is shown, at most a few times a second.</summary>
    private void Progress(TransferUpdate update)
    {
        _lastUpdate = update;
        if (_updatePending) return;
        _updatePending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(150);
            _updatePending = false;
            if (_lastUpdate is not { } last) return;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = Math.Max(1, last.FilesTotal);
            ProgressBar.Value = last.FilesDone;
            var file = last.Total > 0 && last.Done < last.Total ? $" ({ByteSize.Format(last.Done)} of {ByteSize.Format(last.Total)})" : "";
            ProgressText.Text = $"{last.Phase} {last.Path}{file} · {last.FilesDone} of {last.FilesTotal}";
        }, DispatcherPriority.Background);
    }

    // ─── History ───────────────────────────────────────────────────────────

    // The history's selection cleared by the view, not chosen.
    private bool _restoringHistory;

    private void HistoryToggle_Click(object? sender, RoutedEventArgs e)
    {
        var open = HistoryToggle.IsChecked == true;
        var row = ScmView.RowDefinitions[7];
        if (!open && row.Height.IsAbsolute) _historyHeight = row.ActualHeight;
        HistoryBody.IsVisible = open;
        HistorySash.IsEnabled = open;
        row.MinHeight = open ? 32 : 0;
        row.Height = open ? new GridLength(_historyHeight) : GridLength.Auto;
    }

    private void LoadHistory()
    {
        if (Selected is not { } s)
        {
            HistoryList.ItemsSource = null;
            HistoryMessage.IsVisible = false;
            return;
        }
        var rows = _files.History(s.Connection.Id).Select(o => new OperationRow(o)).ToList();
        _restoringHistory = true;
        try { HistoryList.ItemsSource = rows; }
        finally { _restoringHistory = false; }
        HistoryMessage.IsVisible = rows.Count == 0;
    }

    private DeployOperation? ChosenOperation => (HistoryList.SelectedItem as OperationRow)?.Operation;

    private void Operation_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringHistory || ChosenOperation is not { } op) return;
        _diffLoad?.Cancel();
        _revealing = true;
        try { Changes.SelectedItem = null; }
        finally { _revealing = false; }
        FileView.IsVisible = false;
        OperationView.IsVisible = true;
        EmptyState.IsVisible = false;
        var row = new OperationRow(op);
        OperationTitle.Text = $"{row.Arrow} #{op.Id} {op.Message}";
        OperationText.Text = $"{op.Kind} on {op.ConnectionName} ({op.Environment}) by {op.User}, {op.StartedUtc.ToLocalTime():g} - {row.Result}.";
        OperationFiles.ItemsSource = _files.Files(op.Id).Select(f => new OperationFileRow(f)).ToList();
        RollbackButton.IsEnabled = row.CanRollBack;
        OpenReviewButton.IsVisible = op.ReviewFolder is not null;
        RestoreButton.IsEnabled = false;
        HistoryDiff.ShowMessage("Choose a file to see what the operation changed.");
        if (OperationFiles.ItemCount > 0) OperationFiles.SelectedIndex = 0;
    }

    private async void OperationFile_Changed(object? sender, SelectionChangedEventArgs e)
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
            await HistoryDiff.ShowDiffAsync(file.Path, before, after, "Before", "After");
    }

    private async void Rollback_Click(object? sender, RoutedEventArgs e)
    {
        if (ChosenOperation is not { } op || Selected is not { } s) return;
        var where = op.Kind == DeployOperationKind.Push ? $"on {s.Name} ({s.Connection.Environment})" : "in the project";
        if (!await Dialogs.ConfirmAsync($"Roll back #{op.Id} - \"{op.Message}\"?{Environment.NewLine}{Environment.NewLine}" +
                                        $"Its {op.FileCount} file(s) {where} get back what they had before it: a file it added is deleted. " +
                                        "Only when each is still as it left it - a file changed since stops the rollback before anything changes.",
                "Roll back", "Cancel", danger: true))
            return;
        var request = new RollbackRequest(s.Connection.Id, op.Id, s.Root, "", Progress);
        if (await Run($"Roll back #{op.Id} of '{s.Site}'", (sp, reporter, ct) => sp.GetRequiredService<RollbackOperationUseCase>().ExecuteAsync(request, reporter, ct)))
            Say($"#{op.Id} is rolled back.");
        LoadHistory();
        ShowFile(null);
        await ScanAsync();
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (OperationFiles.SelectedItem is not OperationFileRow { File: { BeforeHash: { } hash } file } || ChosenOperation is not { } op || Selected is not { } s) return;
        if (!await Dialogs.ConfirmAsync($"Put {file.Path} back in the project as it was before #{op.Id}?{Environment.NewLine}{Environment.NewLine}" +
                                        "The project's version now is kept first. Nothing on the server changes - push it to send it on.",
                "Restore it", "Cancel"))
            return;
        var request = new RestoreVersionRequest(s.Connection.Id, s.Root, file.Path, hash, op.Id);
        if (await Run($"Restore {file.Path} as of #{op.Id}", (sp, reporter, ct) => sp.GetRequiredService<RestoreVersionUseCase>().ExecuteAsync(request, reporter, ct)))
            Say($"{file.Path} is back as it was before #{op.Id}.");
        LoadHistory();
        ShowFile(null);
        await ScanAsync();
    }

    private async void OpenReview_Click(object? sender, RoutedEventArgs e)
    {
        if (ChosenOperation?.ReviewFolder is { } folder && Directory.Exists(folder)) await OpenFolderAsync(folder);
    }

    // ─── The window ────────────────────────────────────────────────────────

    private MainWindow? Main => TopLevel.GetTopLevel(this) as MainWindow;

    private void Say(string text) => Main?.Say(text);

    private async Task OpenFolderAsync(string folder)
    {
        if (TopLevel.GetTopLevel(this) is { } top && Directory.Exists(folder)) await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy - nothing copied.
        }
    }
}
