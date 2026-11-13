using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DnnManager.Application.Deployment;
using DnnManager.Desktop.Services;

namespace DnnManager.Desktop.Views;

/// <summary>
/// Source Control's other side bar views - VS Code's. The Explorer: the open editors (the tabs) and the chosen server's
/// project folder as a tree, as a push sees it (the server's ignore rules), a changed file with its letter; a click opens
/// a file as it is. Search: every match in the project's text files - match case, whole word, regular expression - by
/// file; a click opens the file at the match. Replace All writes the project's files on this computer (each in its own
/// encoding and line endings) - the server only changes when they are pushed. As the Windows app's SourceControlPage.Views.
/// </summary>
public partial class SourceControlView
{
    private readonly ObservableCollection<ExplorerRow> _explorerRows = [];
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    // The tree was listed for another server, or before a comparison: listed again when the Explorer is shown.
    private bool _explorerStale = true;
    private CancellationTokenSource? _explorerLoad;

    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _searchRun;
    private FindResult? _found;
    private FindQuery? _foundQuery;
    private readonly HashSet<string> _collapsedResults = new(StringComparer.OrdinalIgnoreCase);

    private void ServerChangedForViews()
    {
        _expandedFolders.Clear();
        _explorerRows.Clear();
        _explorerStale = true;
        ProjectFilesTitle.Text = Selected is { } s ? s.Site.ToUpperInvariant() : "PROJECT";
        if (CurrentView == SideView.Explorer) _ = LoadExplorerAsync();
        _found = null;
        _collapsedResults.Clear();
        FillSearch();
        if ((SearchText.Text ?? "").Length > 0) _ = SearchAsync();
    }

    // ─── Explorer ──────────────────────────────────────────────────────────

    private IgnoreRules Rules => Selected is { } s ? IgnoreRules.Parse(s.Connection.IgnoreRules) : IgnoreRules.None;

    // Each changed file of the project, by path: its letter in the tree.
    private Dictionary<string, string> Letters() =>
        _scan is null ? new(StringComparer.OrdinalIgnoreCase)
            : _scan.Changes.Where(i => i.Local is not null).ToDictionary(i => i.Path, i => ChangeRow.LetterOf(i), StringComparer.OrdinalIgnoreCase);

    /// <summary>The project's folder listed again - the folders open stay open.</summary>
    internal async Task LoadExplorerAsync()
    {
        _explorerLoad?.Cancel();
        if (Selected is not { } server)
        {
            _explorerRows.Clear();
            ExplorerMessage.Text = ServersList.ItemCount > 0 ? "Choose a server in Source Control to see its project's files." : "Add a server to a project to see its files.";
            ExplorerMessage.IsVisible = true;
            return;
        }
        var load = _explorerLoad = new CancellationTokenSource();
        var rules = Rules;
        var open = _expandedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProjectFilesTitle.Text = server.Site.ToUpperInvariant();
        try
        {
            var entries = await Task.Run(() =>
            {
                var list = new List<(ProjectEntry Entry, int Depth)>();
                void Add(string folder, int depth)
                {
                    foreach (var entry in ProjectFiles.List(server.Root, folder, rules))
                    {
                        load.Token.ThrowIfCancellationRequested();
                        list.Add((entry, depth));
                        if (entry.IsFolder && open.Contains(entry.Path)) Add(entry.Path, depth + 1);
                    }
                }
                Add("", 0);
                return list;
            }, load.Token);
            if (load.IsCancellationRequested) return;
            var letters = Letters();
            var chosen = (ExplorerTree.SelectedItem as ExplorerRow)?.Path;
            _explorerRows.Clear();
            foreach (var (entry, depth) in entries)
                _explorerRows.Add(new ExplorerRow(entry, depth, letters.GetValueOrDefault(entry.Path, "")) { Expanded = entry.IsFolder && open.Contains(entry.Path) });
            ExplorerTree.SelectedItem = _explorerRows.FirstOrDefault(r => r.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            _explorerStale = false;
            ExplorerMessage.Text = _explorerRows.Count == 0 ? $"{server.Root} has no files - or the server's ignore rules leave them all out." : "";
            ExplorerMessage.IsVisible = _explorerRows.Count == 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _explorerRows.Clear();
            ExplorerMessage.Text = $"Couldn't list {server.Root}: {ex.Message}";
            ExplorerMessage.IsVisible = true;
        }
    }

    // A comparison: the letters follow - the tree listed again now if it is shown, else when it is.
    private void ExplorerFollowScan()
    {
        _explorerStale = true;
        if (CurrentView == SideView.Explorer) _ = LoadExplorerAsync();
    }

    /// <summary>Opens or closes <paramref name="row"/>'s folder - its files listed when it opens.</summary>
    internal async Task ToggleFolderAsync(ExplorerRow row)
    {
        if (!row.IsFolder || Selected is not { } server) return;
        var at = _explorerRows.IndexOf(row);
        if (at < 0) return;
        if (row.Expanded)
        {
            row.Expanded = false;
            _expandedFolders.Remove(row.Path);
            _expandedFolders.RemoveWhere(p => p.StartsWith(row.Path + "/", StringComparison.OrdinalIgnoreCase));
            while (at + 1 < _explorerRows.Count && _explorerRows[at + 1].Depth > row.Depth) _explorerRows.RemoveAt(at + 1);
            return;
        }
        row.Expanded = true;
        _expandedFolders.Add(row.Path);
        var rules = Rules;
        try
        {
            var children = await Task.Run(() => ProjectFiles.List(server.Root, row.Path, rules));
            // Closed again, or the tree listed anew, meanwhile.
            at = _explorerRows.IndexOf(row);
            if (at < 0 || !row.Expanded) return;
            var letters = Letters();
            foreach (var child in children)
                _explorerRows.Insert(++at, new ExplorerRow(child, row.Depth + 1, letters.GetValueOrDefault(child.Path, "")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.Expanded = false;
            _expandedFolders.Remove(row.Path);
            ExplorerMessage.Text = $"Couldn't open {row.Path}: {ex.Message}";
            ExplorerMessage.IsVisible = true;
        }
    }

    private void ExplorerItem_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ExplorerRow row) return;
        if (row.IsFolder) _ = ToggleFolderAsync(row);
        else OpenFile(row.Path, keep: false);
    }

    private void ExplorerItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ExplorerRow { IsFolder: false } row) OpenFile(row.Path, keep: true);
    }

    // Enter or Space opens a file or folder; → opens a folder, ← closes it or goes to the folder it is in - as VS Code's tree.
    private void ExplorerTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerRow row) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space when row.IsFolder:
                _ = ToggleFolderAsync(row);
                break;
            case Key.Enter or Key.Space:
                OpenFile(row.Path, keep: e.Key == Key.Enter);
                break;
            case Key.Right when row.IsFolder && !row.Expanded:
                _ = ToggleFolderAsync(row);
                break;
            case Key.Left when row.IsFolder && row.Expanded:
                _ = ToggleFolderAsync(row);
                break;
            case Key.Left when row.Depth > 0:
                var parent = _explorerRows.Take(_explorerRows.IndexOf(row)).LastOrDefault(r => r.Depth == row.Depth - 1);
                if (parent is not null)
                {
                    ExplorerTree.SelectedItem = parent;
                    ExplorerTree.ContainerFromItem(parent)?.Focus();
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ExplorerRefresh_Click(object? sender, RoutedEventArgs e) => _ = LoadExplorerAsync();

    private void ExplorerCollapse_Click(object? sender, RoutedEventArgs e)
    {
        _expandedFolders.Clear();
        _ = LoadExplorerAsync();
    }

    private async void ExplorerCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is ExplorerRow row) await CopyAsync(row.Path);
    }

    private async void ExplorerReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerRow row || Selected is not { } s) return;
        try
        {
            var full = ProjectFiles.Full(s.Root, row.Path);
            await OpenFolderAsync(row.IsFolder ? full : Path.GetDirectoryName(full)!);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ─── Search ────────────────────────────────────────────────────────────

    private FindQuery Query => new(SearchText.Text ?? "", MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);

    private bool Replacing => ReplaceRow.IsVisible;

    // Typing searches after a pause, as VS Code does; Enter at once.
    private void SearchText_Changed(object? sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void ReplaceText_Changed(object? sender, TextChangedEventArgs e) => FillSearch();

    private void FindOption_Click(object? sender, RoutedEventArgs e) => _ = SearchAsync();

    private void ReplaceToggle_Click(object? sender, RoutedEventArgs e)
    {
        ReplaceRow.IsVisible = ReplaceToggle.IsChecked == true;
        FillSearch();
        if (Replacing) ReplaceText.Focus();
    }

    // Enter: search now. Ctrl+Alt+Enter: replace all. Alt+C, Alt+W, Alt+R: the toggles - VS Code's keys.
    private void SearchText_KeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key == Key.Enter && mods == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            if (ReplaceAllButton.IsEnabled) ReplaceAll_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Enter && mods == KeyModifiers.None)
        {
            _searchDelay.Stop();
            _ = SearchAsync();
        }
        else if (mods == KeyModifiers.Alt && e.Key is Key.C or Key.W or Key.R)
        {
            var toggle = e.Key switch { Key.C => MatchCase, Key.W => WholeWord, _ => UseRegex };
            toggle.IsChecked = toggle.IsChecked != true;
            _ = SearchAsync();
        }
        else return;
        e.Handled = true;
    }

    /// <summary>Searches the chosen server's project folder - the last search asked for wins, an older one is stopped.</summary>
    internal async Task SearchAsync()
    {
        _searchRun?.Cancel();
        var query = Query;
        SearchSummary.ClearValue(TextBlock.ForegroundProperty);
        if (query.Text.Length == 0 || Selected is not { } server)
        {
            _found = null;
            FillSearch();
            SearchSummary.Text = query.Text.Length > 0
                ? ServersList.ItemCount > 0 ? "Choose a server in Source Control to search its project's files." : "Add a server to a project to search its files."
                : "";
            return;
        }
        if (query.ToRegex(out var problem) is null)
        {
            _found = null;
            FillSearch();
            SearchSummary.Text = $"Not a regular expression: {problem}";
            SearchSummary.Foreground = Res.Brush("ErrorText");
            return;
        }
        var run = _searchRun = new CancellationTokenSource();
        var rules = Rules;
        SearchSummary.Text = "Searching…";
        try
        {
            var found = await Task.Run(() => ProjectFiles.Find(server.Root, rules, query, run.Token), run.Token);
            if (run.IsCancellationRequested) return;
            _found = found;
            _foundQuery = query;
            _collapsedResults.Clear();
            FillSearch();
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is AggregateException { InnerException: OperationCanceledException })
        {
        }
    }

    /// <summary>The results shown - for a test.</summary>
    internal IReadOnlyList<SearchRow> SearchRows => SearchResults.ItemsSource as IReadOnlyList<SearchRow> ?? [];

    // The results as rows: each file, then its matches unless it is collapsed - with what Replace will write.
    private void FillSearch()
    {
        var rows = new List<SearchRow>();
        var replacing = Replacing && _found is not null;
        var replacement = ReplaceText.Text ?? "";
        var regex = replacing && _foundQuery is { UseRegex: true } q ? q.ToRegex(out _) : null;
        foreach (var file in _found?.Files ?? [])
        {
            var collapsed = _collapsedResults.Contains(file.Path);
            rows.Add(new SearchRow { File = file, Collapsed = collapsed });
            if (collapsed) continue;
            foreach (var match in file.Matches)
                rows.Add(new SearchRow
                {
                    File = file, Match = match, Replacing = replacing,
                    // A regular expression's $1 and the like, as the match makes them.
                    Replacement = !replacing ? "" : regex?.Match(match.Text) is { Success: true } m ? m.Result(replacement) : replacement
                });
        }
        SearchResults.ItemsSource = rows;
        ReplaceAllButton.IsEnabled = Replacing && _found is { MatchCount: > 0 };
        if (_found is not { } found) return;
        SearchSummary.ClearValue(TextBlock.ForegroundProperty);
        SearchSummary.Text = found.MatchCount == 0
            ? $"No results - {found.Searched:N0} files searched (the server's ignore rules leave some out)."
            : $"{found.MatchCount:N0} result{(found.MatchCount == 1 ? "" : "s")} in {found.Files.Count:N0} file{(found.Files.Count == 1 ? "" : "s")}" +
              (found.Truncated ? $" - the first {ProjectFiles.MaxMatches:N0} only; narrow the search to see the rest." : "");
    }

    private void SearchItem_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SearchRow row) OpenResult(row, keep: false);
    }

    private void SearchItem_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SearchRow { IsFile: false } row) OpenResult(row, keep: true);
    }

    // A file's row opens or closes it; a match opens the file there.
    internal void OpenResult(SearchRow row, bool keep)
    {
        if (row.Match is not { } match)
        {
            if (!_collapsedResults.Remove(row.Path)) _collapsedResults.Add(row.Path);
            FillSearch();
            SearchResults.SelectedItem = SearchRows.FirstOrDefault(r => r.IsFile && r.Path == row.Path);
            return;
        }
        OpenFile(row.Path, keep, (match.Line, match.Start, match.Length));
    }

    private void SearchResults_KeyDown(object? sender, KeyEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchRow row) return;
        if (e.Key == Key.Enter || e.Key == Key.Space || (row.IsFile && (e.Key == Key.Left && !row.Collapsed || e.Key == Key.Right && row.Collapsed)))
        {
            OpenResult(row, keep: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void SearchRefresh_Click(object? sender, RoutedEventArgs e) => _ = SearchAsync();

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        _searchRun?.Cancel();
        SearchText.Text = "";
        ReplaceText.Text = "";
        _searchDelay.Stop();
        _found = null;
        _collapsedResults.Clear();
        FillSearch();
        SearchSummary.Text = "";
        SearchText.Focus();
    }

    private void SearchCollapse_Click(object? sender, RoutedEventArgs e)
    {
        if (_found is null) return;
        // All collapsed: expanded again - as VS Code's button, which turns into Expand All.
        if (_found.Files.All(f => _collapsedResults.Contains(f.Path))) _collapsedResults.Clear();
        else foreach (var file in _found.Files) _collapsedResults.Add(file.Path);
        FillSearch();
    }

    /// <summary>
    /// Replace All: every match shown, in its file - asked first, as it changes the project's files on this computer (the
    /// server only when they are pushed). Each file is written whole, in its own encoding and line endings; one that can't
    /// be is left as it was and named.
    /// </summary>
    private async void ReplaceAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_found is not { MatchCount: > 0 } found || _foundQuery is not { } query || Selected is not { } server) return;
        var replacement = ReplaceText.Text ?? "";
        var with = replacement.Length == 0 ? "nothing (the matches are deleted)" : $"'{replacement}'";
        if (!await Dialogs.ConfirmAsync($"Replace {found.MatchCount:N0} match{(found.MatchCount == 1 ? "" : "es")} of '{query.Text}' in {found.Files.Count:N0} " +
                                        $"file{(found.Files.Count == 1 ? "" : "s")} with {with}?{Environment.NewLine}{Environment.NewLine}" +
                                        $"The files in {server.Root} are changed on this computer and can't be put back from here. The server only changes when you push them.",
                "Replace all", "Cancel", danger: true))
            return;
        ReplaceAllButton.IsEnabled = false;
        SearchSummary.Text = "Replacing…";
        var paths = found.Files.Select(f => f.Path).ToList();
        var done = await Task.Run(() => ProjectFiles.Replace(server.Root, paths, query, replacement, CancellationToken.None));
        // The files as they are now: searched again; the tab showing one of them shows it again.
        await SearchAsync();
        if (ActiveTab is { IsFile: true } tab && paths.Contains(tab.Path, StringComparer.OrdinalIgnoreCase)) _ = ShowTabAsync(tab);
        SearchSummary.ClearValue(TextBlock.ForegroundProperty);
        SearchSummary.Text = $"Replaced {done.Replacements:N0} in {done.Files:N0} file{(done.Files == 1 ? "" : "s")} - compare (F5) to see them as changes." +
                             (done.Failed.Count > 0 ? $"{Environment.NewLine}Not replaced in {done.Failed.Count}: {string.Join("; ", done.Failed.Take(5))}" : "");
        if (done.Failed.Count > 0) SearchSummary.Foreground = Res.Brush("ErrorText");
    }
}
