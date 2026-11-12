using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DnnManager.Application.Deployment;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages;

/// <summary>The side bar's views, as VS Code's: Source Control, the Explorer, Search.</summary>
public enum SideView { SourceControl, Explorer, Search }

/// <summary>
/// The Deploy page's other side bar views, beside Source Control - VS Code's. The Explorer: the open editors (the tabs)
/// and the chosen server's project folder as a tree, as a push sees it (the server's ignore rules), a changed file with
/// its letter; a click opens a file as it is. Search: every match in the project's text files - match case, whole word,
/// regular expression - by file; a click opens the file at the match. Replace All writes the project's files on this
/// PC (each in its own encoding and line endings) - the server only changes when they are pushed.
/// </summary>
public partial class SourceControlPage
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

    private void InitializeViews()
    {
        ExplorerTree.ItemsSource = _explorerRows;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = SearchAsync();
        };
        FollowOpenEditors();
    }

    /// <summary>Shows <paramref name="view"/> in the side bar - Ctrl+Shift+G, Ctrl+Shift+E, Ctrl+Shift+F - and puts the keyboard in it.</summary>
    public void ShowView(SideView view)
    {
        var tab = view switch { SideView.Explorer => ExplorerTab, SideView.Search => SearchTab, _ => ScmTab };
        tab.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
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

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        var view = CurrentView;
        ScmView.Visibility = view == SideView.SourceControl ? Visibility.Visible : Visibility.Collapsed;
        ExplorerView.Visibility = view == SideView.Explorer ? Visibility.Visible : Visibility.Collapsed;
        SearchView.Visibility = view == SideView.Search ? Visibility.Visible : Visibility.Collapsed;
        if (view == SideView.Explorer && _explorerStale) _ = LoadExplorerAsync();
    }

    /// <summary>How many files differ, on Source Control's icon - VS Code's badge.</summary>
    private void UpdateBadge()
    {
        var count = _scan?.Changes.Count() ?? 0;
        ChangeBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangeBadgeText.Text = count > 999 ? "999+" : count.ToString();
        System.Windows.Automation.AutomationProperties.SetName(ScmTab, count > 0 ? $"Source Control, {count} changes" : "Source Control");
    }

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
        if (SearchText.Text.Length > 0) _ = SearchAsync();
    }

    // ─── Open editors ──────────────────────────────────────────────────────

    // The open editors follow the tabs: the chosen one chosen here too.
    private void FollowOpenEditors()
    {
        if (!IsInitialized) return;
        OpenEditorsMessage.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _followingTabs = true;
        try { OpenEditors.SelectedItem = ActiveTab; }
        finally { _followingTabs = false; }
    }

    private bool _followingTabs;

    private void OpenEditors_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_followingTabs || OpenEditors.SelectedItem is not EditorTab tab) return;
        Activate(tab);
    }

    // ─── Explorer ──────────────────────────────────────────────────────────

    private IgnoreRules Rules => Selected is { } s ? IgnoreRules.Parse(s.Connection.IgnoreRules) : IgnoreRules.None;

    // Each changed file of the project, by path: its letter in the tree.
    private Dictionary<string, string> Letters() =>
        _scan is null ? new(StringComparer.OrdinalIgnoreCase)
            : _scan.Changes.Where(i => i.Local is not null).ToDictionary(i => i.Path, i => ChangeRow.LetterOf(i), StringComparer.OrdinalIgnoreCase);

    /// <summary>The project's folder listed again - the folders open stay open.</summary>
    private async Task LoadExplorerAsync()
    {
        _explorerLoad?.Cancel();
        if (Selected is not { } server)
        {
            _explorerRows.Clear();
            ExplorerMessage.Text = "Add a server to a project to see its files.";
            ExplorerMessage.Visibility = Visibility.Visible;
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
            ExplorerMessage.Visibility = _explorerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _explorerRows.Clear();
            ExplorerMessage.Text = $"Couldn't list {server.Root}: {ex.Message}";
            ExplorerMessage.Visibility = Visibility.Visible;
        }
    }

    // A comparison: the letters follow - the tree listed again now if it is shown, else when it is.
    private void ExplorerFollowScan()
    {
        _explorerStale = true;
        if (CurrentView == SideView.Explorer) _ = LoadExplorerAsync();
    }

    /// <summary>Opens or closes <paramref name="row"/>'s folder - its files listed when it opens.</summary>
    private async Task ToggleFolderAsync(ExplorerRow row)
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
            ExplorerMessage.Visibility = Visibility.Visible;
        }
    }

    private void ExplorerItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExplorerRow row) return;
        if (row.IsFolder) _ = ToggleFolderAsync(row);
        else OpenFile(row.Path, keep: false);
    }

    private void ExplorerItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExplorerRow { IsFolder: false } row) OpenFile(row.Path, keep: true);
    }

    // Enter or Space opens a file or folder; → opens a folder, ← closes it or goes to the folder it is in - as VS Code's tree.
    private void ExplorerTree_KeyDown(object sender, KeyEventArgs e)
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
                    (ExplorerTree.ItemContainerGenerator.ContainerFromItem(parent) as ListBoxItem)?.Focus();
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ExplorerRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadExplorerAsync();

    private void ExplorerCollapse_Click(object sender, RoutedEventArgs e)
    {
        _expandedFolders.Clear();
        _ = LoadExplorerAsync();
    }

    private void ExplorerCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is ExplorerRow row)
            try { Clipboard.SetText(row.Path); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void ExplorerReveal_Click(object sender, RoutedEventArgs e)
    {
        if (ExplorerTree.SelectedItem is not ExplorerRow row || Selected is not { } s) return;
        try
        {
            var full = ProjectFiles.Full(s.Root, row.Path);
            Shell.Open(row.IsFolder ? full : Path.GetDirectoryName(full)!);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ─── Search ────────────────────────────────────────────────────────────

    private FindQuery Query => new(SearchText.Text, MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);

    private bool Replacing => ReplaceRow.Visibility == Visibility.Visible;

    // Typing searches after a pause, as VS Code does; Enter at once.
    private void SearchText_Changed(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void ReplaceText_Changed(object sender, TextChangedEventArgs e) => FillSearch();

    private void FindOption_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void ReplaceToggle_Click(object sender, RoutedEventArgs e)
    {
        ReplaceRow.Visibility = ReplaceToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FillSearch();
        if (Replacing) ReplaceText.Focus();
    }

    // Enter: search now. Ctrl+Alt+Enter: replace all. Alt+C, Alt+W, Alt+R: the toggles - VS Code's keys.
    private void SearchText_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Enter && mods == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (ReplaceAllButton.IsEnabled) ReplaceAll_Click(this, new RoutedEventArgs());
        }
        else if (key == Key.Enter && mods == ModifierKeys.None)
        {
            _searchDelay.Stop();
            _ = SearchAsync();
        }
        else if (mods == ModifierKeys.Alt && key is Key.C or Key.W or Key.R)
        {
            var toggle = key switch { Key.C => MatchCase, Key.W => WholeWord, _ => UseRegex };
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
        if (query.Text.Length == 0 || Selected is not { } server)
        {
            _found = null;
            FillSearch();
            SearchSummary.Text = query.Text.Length > 0 ? "Add a server to a project to search its files." : "";
            return;
        }
        if (query.ToRegex(out var problem) is null)
        {
            _found = null;
            FillSearch();
            SearchSummary.Text = $"Not a regular expression: {problem}";
            SearchSummary.Foreground = (System.Windows.Media.Brush)FindResource("ErrorText");
            return;
        }
        var run = _searchRun = new CancellationTokenSource();
        var rules = Rules;
        SearchSummary.ClearValue(TextBlock.ForegroundProperty);
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

    // The results as rows: each file, then its matches unless it is collapsed - with what Replace will write.
    private void FillSearch()
    {
        if (!IsInitialized) return;
        var rows = new List<SearchRow>();
        var replacing = Replacing && _found is not null;
        var replacement = ReplaceText.Text;
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

    private void SearchItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchRow row) OpenResult(row, keep: false);
    }

    private void SearchItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SearchRow { IsFile: false } row) OpenResult(row, keep: true);
    }

    // A file's row opens or closes it; a match opens the file there.
    private void OpenResult(SearchRow row, bool keep)
    {
        if (row.Match is not { } match)
        {
            if (!_collapsedResults.Remove(row.Path)) _collapsedResults.Add(row.Path);
            FillSearch();
            SearchResults.SelectedItem = ((IEnumerable<SearchRow>)SearchResults.ItemsSource).FirstOrDefault(r => r.IsFile && r.Path == row.Path);
            return;
        }
        OpenFile(row.Path, keep, (match.Line, match.Start, match.Length));
    }

    private void SearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchRow row) return;
        if (e.Key == Key.Enter || e.Key == Key.Space || (row.IsFile && (e.Key == Key.Left && !row.Collapsed || e.Key == Key.Right && row.Collapsed)))
        {
            OpenResult(row, keep: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void SearchRefresh_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void SearchClear_Click(object sender, RoutedEventArgs e)
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

    private void SearchCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;
        // All collapsed: expanded again - as VS Code's button, which turns into Expand All.
        if (_found.Files.All(f => _collapsedResults.Contains(f.Path))) _collapsedResults.Clear();
        else foreach (var file in _found.Files) _collapsedResults.Add(file.Path);
        FillSearch();
    }

    /// <summary>
    /// Replace All: every match shown, in its file - asked first, as it changes the project's files on this PC (the server
    /// only when they are pushed). Each file is written whole, in its own encoding and line endings; one that can't be is
    /// left as it was and named.
    /// </summary>
    private async void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_found is not { MatchCount: > 0 } found || _foundQuery is not { } query || Selected is not { } server) return;
        var replacement = ReplaceText.Text;
        var with = replacement.Length == 0 ? "nothing (the matches are deleted)" : $"'{replacement}'";
        if (!Dialogs.ConfirmDanger($"Replace {found.MatchCount:N0} match{(found.MatchCount == 1 ? "" : "es")} of '{query.Text}' in {found.Files.Count:N0} " +
                                   $"file{(found.Files.Count == 1 ? "" : "s")} with {with}?{Environment.NewLine}{Environment.NewLine}" +
                                   $"The files in {server.Root} are changed on this PC and can't be put back from here. The server only changes when you push them.",
                "Replace all", "Cancel"))
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
        if (done.Failed.Count > 0) SearchSummary.Foreground = (System.Windows.Media.Brush)FindResource("ErrorText");
    }
}
