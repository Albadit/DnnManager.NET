using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

/// <summary>
/// Source Control - VS Code's, for a project's servers, as the Windows app's (Pages/SourceControlPage). SERVERS lists
/// every project's servers - the projects are the folders in the projects folder, and every project that has a server -
/// and nothing is read from a server until one is chosen: choosing one compares it (Ctrl+click for more than one, each
/// compared in turn, one shown at a time). For the server shown: what differs - staged, to push, to pull, conflicts -
/// staged with + and pushed or pulled after a review; the history, each push or pull rolled back from here. The
/// editor shows the chosen file's diff. Everything that touches a server or a project's files runs as an operation
/// (<see cref="OperationRunner"/>): in Output, cancellable, undone when it fails.
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
    private SyncScan? _scan;
    private ServerRow? _active;
    private SyncItem? _shown;
    private bool _restoringServers, _comparing;
    private CancellationTokenSource? _diffLoad;
    private TransferUpdate? _lastUpdate;
    private bool _updatePending;
    private IReadOnlyList<DiffLine>? _diffLines;

    public SourceControlView()
    {
        InitializeComponent();
        _connections = _services.GetRequiredService<DeployConnections>();
        _files = _services.GetRequiredService<DeployFiles>();
        _options = _services.GetRequiredService<IOptions<AppOptions>>().Value;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                _ = ScanAsync();
                e.Handled = true;
            }
        };
        LoadServers(null);
    }

    private OperationRunner Runner => _services.GetRequiredService<OperationRunner>();

    /// <summary>The servers chosen in SERVERS.</summary>
    internal IReadOnlyList<ServerRow> ChosenServers => ServersList.SelectedItems?.OfType<ServerRow>().ToList() ?? [];

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
        NoServers.IsVisible = rows.Count == 0;
        CommitArea.IsEnabled = ScanButton.IsEnabled = rows.Count > 0;
        if (rows.Count == 0 || _active is null) ShowScan();
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
            var item = new MenuItem { Header = $"{(server.Connection.Id == _active?.Connection.Id ? "✓ " : "   ")}{server.Title}  {server.Counts}" };
            item.Click += (_, _) => ShowServer(server);
            menu.Items.Add(item);
        }
        menu.Open(ChangesServer);
    }

    /// <summary>Shows <paramref name="server"/>'s changes and history - one of the servers chosen; the others stay chosen.</summary>
    internal void ShowServer(ServerRow server)
    {
        if (server.Connection.Id == _active?.Connection.Id) return;
        _active = server;
        ActiveChanged();
        FollowChosen();
    }

    private void ActiveChanged()
    {
        _staged.Clear();
        _scan = _active is { } s ? _scans.GetValueOrDefault(s.Connection.Id) : null;
        ShowDiffMessage("Choose a file in CHANGES to see what differs.");
        ShowScan();
        LoadHistory();
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var projects = Projects().Select(p => p.Name).ToList();
        if (await ServerDialog.ShowAsync(_connections, _options.BaseDirectory, projects, null) is { } added) LoadServers(added.Id);
    }

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } s) return;
        var current = _connections.Find(s.Connection.Id) ?? s.Connection;
        if (await ServerDialog.ShowAsync(_connections, _options.BaseDirectory, [s.Site], current) is not { } edited) return;
        // A comparison of the server as it was no longer says how it is.
        _scans.Remove(edited.Id);
        if (_active?.Connection.Id == edited.Id) _scan = null;
        LoadServers(edited.Id);
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } s || _connections.Find(s.Connection.Id) is not { } connection || !await EnsurePasswordAsync(connection)) return;
        Say($"Testing {connection.Name}…");
        var check = await Task.Run(() => _connections.TestAsync(connection, _connections.Secrets(connection.Id), CancellationToken.None));
        _connections.RecordCheck(connection.Id, check);
        LoadServers(null);
        await Dialogs.MessageAsync(check.Ok ? check.Message : $"{check.Message}{(check.NeedsTrust ? " Edit the server and test it there to check and trust its identity." : "")}",
            check.Ok ? "Connected" : "Couldn't connect");
        Say("");
    }

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } s) return;
        if (!await Dialogs.ConfirmAsync($"Remove {s.Name} from {s.Site}? Its password, comparison baseline and history in DNN Manager are deleted. " +
                                        "The project's files and the server's stay as they are.", "Remove", "Cancel", danger: true))
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
        if (RowOf(sender) is not { } s || !Directory.Exists(s.Root)) return;
        if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(s.Root));
    }

    private void CompareOne_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } s) _ = ScanAsync([s]);
    }

    private ServerRow? RowOf(object? sender) => (sender as Control)?.Tag as ServerRow ?? _active;

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
        ShowProgress($"Comparing {connection.Site} with {connection.Name}…");
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
        UntrustedServerException { FirstTime: true } => $"{ex.Message} Edit the server and test it to check and trust it.",
        UntrustedServerException => $"{ex.Message} Edit the server and test it to see the new identity.",
        _ => ex.Message
    };

    private void ShowScan()
    {
        if (_scan is null)
        {
            ScanSummary.Text = "";
            Changes.ItemsSource = null;
            ChangesMessage.Text = _active is not null ? "Not compared yet - Compare (F5) to see what differs."
                : ServersList.ItemCount > 0 ? "Choose a server above to compare it with its project - Ctrl+click (⌘+click) to choose more than one." : "";
            ChangesMessage.IsVisible = ChangesMessage.Text.Length > 0;
            UpdateButtons();
            return;
        }
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
        _filling = true;
        try
        {
            Changes.ItemsSource = rows;
            if (chosen is not null) Changes.SelectedItem = rows.FirstOrDefault(r => r.IsFile && r.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        }
        finally { _filling = false; }
        ChangesMessage.Text = changes.Count == 0 ? "The project and the server are the same - nothing to push or pull." : "";
        ChangesMessage.IsVisible = changes.Count == 0;
        UpdateButtons();
    }

    private bool _filling;

    // ─── Staging ───────────────────────────────────────────────────────────

    private void Stage_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ChangeRow { Item: { } item }) Stage([item], true);
    }

    private void Unstage_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ChangeRow { Item: { } item }) Stage([item], false);
    }

    private void StageGroup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not ChangeRow row || _scan is null) return;
        var direction = row.Group == ChangeGroup.Incoming ? SyncDirection.Pull : SyncDirection.Push;
        // Never a file that looks like a secret: each of those is staged by itself, on purpose.
        Stage(_scan.Changes.Where(i => i.Direction == direction && !i.Sensitive), true);
    }

    private void UnstageGroup_Click(object? sender, RoutedEventArgs e)
    {
        _staged.Clear();
        Fill();
    }

    private void Stage(IEnumerable<SyncItem> items, bool stage)
    {
        foreach (var item in items)
        {
            if (!stage) _staged.Remove(item.Path);
            else if (item.Direction is SyncDirection.Push or SyncDirection.Pull) _staged.Add(item.Path);
        }
        Fill();
    }

    private void Header_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChangeRow { IsHeader: true } row) return;
        if (!_collapsed.Remove(row.Group)) _collapsed.Add(row.Group);
        Fill();
    }

    private void Changes_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Changes.SelectedItem is not ChangeRow { Item: { } item } row) return;
        if (e.Key is Key.Space or Key.Enter)
        {
            Stage([item], !row.IsStaged);
            e.Handled = true;
        }
    }

    private IReadOnlyList<SyncItem> StagedItems(SyncDirection direction) =>
        _scan is null ? [] : [.. _scan.Changes.Where(i => i.Direction == direction && _staged.Contains(i.Path))];

    private void UpdateButtons()
    {
        var push = StagedItems(SyncDirection.Push).Count;
        var pull = StagedItems(SyncDirection.Pull).Count;
        PushButton.Content = push > 0 ? $"↑ Push {push}" : "↑ Push";
        PullButton.Content = pull > 0 ? $"↓ Pull {pull}" : "↓ Pull";
        PushButton.IsEnabled = push > 0;
        PullButton.IsEnabled = pull > 0;
        ToolTip.SetTip(PushButton, push > 0 ? $"Push the {push} staged file(s) to the server, after a review" : "Stage files to push with + first");
        ToolTip.SetTip(PullButton, pull > 0 ? $"Pull the {pull} staged file(s) into the project, after a review" : "Stage files to pull with + first");
    }

    private void CommitMessage_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            _ = StagedItems(SyncDirection.Push).Count > 0 ? PushAsync() : PullAsync();
        }
    }

    // ─── The diff ─────────────────────────────────────────────────────────

    private void Change_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (Changes.SelectedItem is ChangeRow { Item: { } item }) _ = ShowFileAsync(item);
    }

    private void ShowDiffMessage(string message)
    {
        _shown = null;
        _diffLines = null;
        DiffTitle.Text = "";
        DiffSides.Text = "";
        ConflictBar.IsVisible = false;
        DiffMessage.Text = message;
        DiffMessage.IsVisible = true;
        UnifiedList.IsVisible = SplitList.IsVisible = false;
    }

    private async Task ShowFileAsync(SyncItem item)
    {
        _diffLoad?.Cancel();
        if (_scan is not { } scan) return;
        if (item.Remote is not null && !await EnsurePasswordAsync(scan.Connection))
        {
            ShowDiffMessage("The server's password wasn't given - its version can't be read.");
            return;
        }
        var load = _diffLoad = new CancellationTokenSource();
        ShowDiffMessage($"Reading {item.Path}…");
        try
        {
            var versions = await Task.Run(() => _files.VersionsAsync(scan, item, load.Token), load.Token);
            if (load.IsCancellationRequested) return;
            if (versions.TooLarge)
            {
                ShowDiffMessage($"{item.Path} is larger than {TextContent.MaxBytes / (1024 * 1024)} MB - too big to show a diff of.");
                return;
            }
            // Old on the left, new on the right: what the push or pull changes.
            var pull = item.Direction == SyncDirection.Pull;
            var (oldBytes, newBytes) = pull ? (versions.Local, versions.Remote) : (versions.Remote, versions.Local);
            var (oldText, newText) = (Decode(oldBytes), Decode(newBytes));
            if (oldText is null || newText is null)
            {
                ShowDiffMessage($"{item.Path} isn't text - {item.Describe()}.");
                _shown = item;
                DiffTitle.Text = item.Path;
                ConflictBar.IsVisible = item.Direction == SyncDirection.Conflict;
                return;
            }
            var lines = await Task.Run(() => TextDiff.Compute(oldText.Lines, newText.Lines), load.Token);
            if (load.IsCancellationRequested) return;
            _shown = item;
            _diffLines = lines;
            DiffTitle.Text = item.Path;
            DiffSides.Text = $"{(pull ? "In the project" : "On the server")} ↔ {(pull ? "On the server" : "In the project")} - {item.Describe()}";
            ConflictBar.IsVisible = item.Direction == SyncDirection.Conflict;
            RenderDiff();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is RemoteException or IOException or UnauthorizedAccessException)
        {
            if (!load.IsCancellationRequested) ShowDiffMessage($"Couldn't read {item.Path}: {Explain(ex)}");
        }

        static TextContent? Decode(byte[]? bytes) => bytes is null ? TextContent.Empty : TextContent.From(bytes);
    }

    private void DiffMode_Click(object? sender, RoutedEventArgs e) => RenderDiff();

    /// <summary>The diff as rows - the changes with three lines around each; side by side or in one column.</summary>
    private void RenderDiff()
    {
        if (_diffLines is not { } lines) return;
        var added = Brush("AddedLineBrush");
        var removed = Brush("RemovedLineBrush");
        var hunks = TextDiff.Hunks(lines);
        if (hunks.Count == 0)
        {
            DiffMessage.Text = "The text is the same - only the line endings, the encoding or the file's bytes differ.";
            DiffMessage.IsVisible = true;
            UnifiedList.IsVisible = SplitList.IsVisible = false;
            return;
        }
        DiffMessage.IsVisible = false;
        if (SideBySide.IsChecked == true)
        {
            var rows = new List<DiffRow>();
            foreach (var hunk in hunks)
            {
                rows.Add(new DiffRow { LeftText = hunk.Header, IsHunk = true });
                foreach (var row in TextDiff.SideBySide(hunk.Lines))
                    rows.Add(new DiffRow
                    {
                        OldNumber = row.Left?.OldNumber?.ToString() ?? "", NewNumber = row.Right?.NewNumber?.ToString() ?? "",
                        LeftText = row.Left?.Text ?? "", RightText = row.Right?.Text ?? "",
                        LeftBackground = row.Left?.Kind == DiffKind.Removed ? removed : null,
                        RightBackground = row.Right?.Kind == DiffKind.Added ? added : null
                    });
            }
            SplitList.ItemsSource = rows;
            SplitList.IsVisible = true;
            UnifiedList.IsVisible = false;
        }
        else
        {
            var rows = new List<DiffRow>();
            foreach (var hunk in hunks)
            {
                rows.Add(new DiffRow { Text = hunk.Header, IsHunk = true });
                rows.AddRange(hunk.Lines.Select(l => new DiffRow
                {
                    OldNumber = l.OldNumber?.ToString() ?? "", NewNumber = l.NewNumber?.ToString() ?? "",
                    Sign = l.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", _ => " " }, Text = l.Text,
                    Background = l.Kind switch { DiffKind.Added => added, DiffKind.Removed => removed, _ => null }
                }));
            }
            UnifiedList.ItemsSource = rows;
            UnifiedList.IsVisible = true;
            SplitList.IsVisible = false;
        }
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    // ─── Conflicts ─────────────────────────────────────────────────────────

    private void KeepLocal_Click(object? sender, RoutedEventArgs e)
    {
        if (_shown is { Direction: SyncDirection.Conflict } item) _ = ResolveAsync(item, ConflictChoice.KeepLocal);
    }

    private async void TakeRemote_Click(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { Direction: SyncDirection.Conflict } item) return;
        if (!await Dialogs.ConfirmAsync($"Take the server's {item.Path}? The project's version is replaced - it is kept first, to roll back to.",
                "Take the server's", "Cancel"))
            return;
        await ResolveAsync(item, ConflictChoice.TakeRemote);
    }

    private async Task ResolveAsync(SyncItem item, ConflictChoice choice)
    {
        if (_scan is not { } scan) return;
        var request = new ResolveRequest(scan, item.Path, choice);
        if (await Run($"Resolve the conflict of {item.Path}", (sp, reporter, ct) => sp.GetRequiredService<ResolveConflictUseCase>().ExecuteAsync(request, reporter, ct)))
            await ScanAsync();
    }

    // ─── Push and pull ─────────────────────────────────────────────────────

    private void Push_Click(object? sender, RoutedEventArgs e) => _ = PushAsync();

    private void Pull_Click(object? sender, RoutedEventArgs e) => _ = PullAsync();

    private async Task PushAsync()
    {
        if (_scan is not { } scan || _connections.Find(scan.Connection.Id) is not { } connection) return;
        var items = StagedItems(SyncDirection.Push);
        if (items.Count == 0) return;
        if (await Dialogs.ReviewAsync(true, connection, items, CommitMessage.Text ?? "") is not { } consent) return;
        var request = new PushRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Push"), consent.AllowSensitive, Progress);
        if (await Run($"Push {items.Count} file(s) of '{connection.Site}' to {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PushChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            Say($"Pushed {items.Count} file(s) to {connection.Name}.");
            AfterCommit(items);
        }
        await ScanAsync();
    }

    private async Task PullAsync()
    {
        if (_scan is not { } scan || _connections.Find(scan.Connection.Id) is not { } connection) return;
        var items = StagedItems(SyncDirection.Pull);
        if (items.Count == 0) return;
        if (await Dialogs.ReviewAsync(false, connection, items, CommitMessage.Text ?? "") is not { } consent) return;
        var review = consent.IntoReviewFolder
            ? Path.Combine(_services.GetRequiredService<AppDataPaths>().DeploymentsDirectory, $"{connection.Site}_review_{DateTime.Now:yyyyMMdd_HHmmss}")
            : null;
        var request = new PullRequest(scan, [.. items.Select(i => i.Path)], Message(items, "Pull"), consent.AllowSensitive, review, Progress);
        if (await Run($"Pull {items.Count} file(s) of '{connection.Site}' from {connection.Name} ({connection.Environment})",
                (sp, reporter, ct) => sp.GetRequiredService<PullChangesUseCase>().ExecuteAsync(request, reporter, ct)))
        {
            Say(review is not null ? $"The server's {items.Count} file(s) are in {review} - the project didn't change." : $"Pulled {items.Count} file(s) from {connection.Name}.");
            AfterCommit(items);
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

    private void ShowProgress(string text)
    {
        ProgressBar.IsIndeterminate = true;
        ProgressText.Text = text;
        ProgressArea.IsVisible = true;
    }

    private void HideProgress() => ProgressArea.IsVisible = false;

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

    private void HistoryToggle_Click(object? sender, RoutedEventArgs e)
    {
        var open = !HistoryList.IsVisible;
        HistoryList.IsVisible = open;
        HistoryTitle.Text = open ? "▾ HISTORY" : "▸ HISTORY";
    }

    private void LoadHistory()
    {
        HistoryList.ItemsSource = _active is { } s ? _files.History(s.Connection.Id).Select(o => new OperationRow(o)).ToList() : null;
        RollbackButton.IsEnabled = false;
    }

    private void Operation_Changed(object? sender, SelectionChangedEventArgs e) =>
        RollbackButton.IsEnabled = HistoryList.SelectedItem is OperationRow { CanRollBack: true };

    private async void Rollback_Click(object? sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not OperationRow { Operation: var op } || _active is not { } s) return;
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
        await ScanAsync();
    }

    // ─── The window ────────────────────────────────────────────────────────

    private MainWindow? Main => TopLevel.GetTopLevel(this) as MainWindow;

    private void Say(string text) => Main?.Say(text);
}
