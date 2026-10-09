using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Sql;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>"Host project": an existing project folder's IIS website, its database, or both.</summary>
public partial class ExistingFolderPage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IProjectRepository _repo;
    private readonly IIisManager _iis;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISqlConnectionTester _tester;
    private readonly AppOptions _opts;
    private CancellationTokenSource? _databaseRead;

    /// <summary>A project folder as the list shows it: its IIS site, and the database its web.config names.</summary>
    /// <param name="state">"Live" (site started), "Offline" (site not started) or "None" (no site) - colours the IIS line.</param>
    public sealed class Folder(string name, string iis, string state) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Iis { get; } = iis;
        public string State { get; } = state;

        /// <summary>"DB: Live", "DB: Offline", "no database"… - empty until web.config is read.</summary>
        public string Database { get; private set; } = "";

        /// <summary>"Live", "Offline" or "None" - colours the database line like <see cref="State"/>.</summary>
        public string DatabaseState { get; private set; } = "None";

        /// <summary>Which database, and what its server said.</summary>
        public string? DatabaseTip { get; private set; }

        public void ShowDatabase(string text, string state, string? tip)
        {
            Database = text; DatabaseState = state; DatabaseTip = tip;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public ExistingFolderPage(OperationRunner runner, IProjectRepository repo, IIisManager iis, IServiceScopeFactory scopes,
        ISqlConnectionTester tester, IOptions<AppOptions> opts)
    {
        _runner = runner; _repo = repo; _iis = iis; _scopes = scopes; _tester = tester; _opts = opts.Value;
        InitializeComponent();
        Refresh();
    }

    private Folder? Selected => FolderList.SelectedItem as Folder;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Reads the folders, their IIS sites and their databases - when the page is first opened, after an operation, and on
    /// Refresh.
    /// </summary>
    public async void Refresh()
    {
        var selected = Selected?.Name;
        RefreshButton.IsEnabled = false;

        // Show which folders already have a website, so the ones still needing one stand out. Reading IIS's
        // configuration takes a moment - off the UI thread, so the page opens at once.
        List<Folder> folders;
        try
        {
            folders = await Task.Run(() =>
            {
                var sites = _iis.GetSiteStates();
                return _repo.ListAllProjectDirectories()
                    .Select(n => !sites.TryGetValue(n, out var state) ? new Folder(n, "no IIS site", "None")
                        : IisStates.IsStarted(state) ? new Folder(n, "IIS: Live", "Live")
                        : new Folder(n, "IIS: Offline", "Offline"))
                    .ToList();
            });
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }

        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = folders.FirstOrDefault(f => f.Name == selected);
        if (folders.Count == 0) EmptyText.Text = $"No project folders found in {_opts.BaseDirectory}.";
        UpdateOptions();
        await ReadDatabasesAsync(folders);
    }

    /// <summary>
    /// Shows whether each folder's database is live - asked with its web.config's connection, the way the Projects table's
    /// SQL column asks (<see cref="SiteDatabaseChecks"/>). After the list is shown: a server that doesn't answer takes a
    /// few seconds. A newer Refresh stops the one before.
    /// </summary>
    private async Task ReadDatabasesAsync(IReadOnlyList<Folder> folders)
    {
        _databaseRead?.Cancel();
        var cts = _databaseRead = new CancellationTokenSource();
        try
        {
            var databases = await Task.Run(() =>
            {
                using var scope = _scopes.CreateScope();
                var container = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>();
                return folders.ToDictionary(f => f.Name, f => container.DatabaseOf(_repo.Build(f.Name)), StringComparer.OrdinalIgnoreCase);
            }, cts.Token);

            var sites = new Dictionary<string, SiteSqlConnection>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                switch (databases[folder.Name])
                {
                    case null:
                        folder.ShowDatabase("no database", "None", "Its web.config names no database of its own yet.");
                        break;
                    case { Kind: DatabaseKind.LocalDbFile } file:
                        // Attached by the site in its own instance - not asked, like the Projects table.
                        folder.ShowDatabase("DB: LocalDB file", "None", $@"App_Data\{file.Database}, attached by the site itself");
                        break;
                    case { } database:
                        folder.ShowDatabase("DB: …", "None", $"Asking {database.Server} about [{database.Database}]…");
                        sites[folder.Name] = LocalSqlContainer.SiteConnection(database);
                        break;
                }
            }
            if (sites.Count == 0) return;

            var checks = await SiteDatabaseChecks.AskAsync(_tester, sites, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var folder in folders)
            {
                if (!sites.TryGetValue(folder.Name, out var site) || !checks.TryGetValue(folder.Name, out var check)) continue;
                if (check is { Reachable: true, Exists: true })
                    folder.ShowDatabase("DB: Live", "Live", $"[{site.Database}] on {site.Server} answers the site's web.config connection");
                else if (check.Reachable)
                    folder.ShowDatabase("DB: not created", "None", check.Problem ?? $"[{site.Database}] isn't on {site.Server}");
                else
                    folder.ShowDatabase("DB: Offline", "Offline", $"{site.Server} doesn't answer the site's web.config connection: {check.Problem}");
            }
        }
        catch (OperationCanceledException)
        {
            // A newer Refresh is reading them.
        }
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOptions();

    private void UpdateOptions()
    {
        if (Selected is not { } folder)
        {
            OptionsCard.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            RunButton.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        OptionsCard.Visibility = Visibility.Visible;
        RunButton.Visibility = Visibility.Visible;
        OptionsTitle.Text = $"'{folder.Name}' - what should be set up?";

        // The folder name becomes the IIS site, app pool and hostname, so it must be a valid project name.
        var check = ProjectName.Validate(folder.Name);
        NameError.Text = check.Success ? "" : $"This folder can't be used as a project: {check.Error} Rename the folder and retry.";
        NameError.Visibility = check.Success ? Visibility.Collapsed : Visibility.Visible;
        FolderOptions.IsEnabled = check.Success;
        RunButton.IsEnabled = check.Success;

        if (check.Success) FolderOptions.Load(_repo.Build(folder.Name));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } folder || !ProjectName.Validate(folder.Name).Success) return;

        var action = FolderOptions.Action;
        var req = new HostExistingProjectRequest
        {
            ProjectName = folder.Name,
            SetupIis = action is ExistingFolderAction.IisOnly or ExistingFolderAction.IisAndDatabase,
            SetupDatabase = FolderOptions.SetupsDatabase,
            BackupFilePath = FolderOptions.SetupsDatabase ? FolderOptions.BackupFile : null
        };
        if (await _runner.RunAsync($"Set up existing project '{folder.Name}'",
                (sp, reporter, ct) => sp.GetRequiredService<HostExistingProjectUseCase>().ExecuteAsync(req, reporter, ct)))
            Toast.Show($"'{folder.Name}' is set up - it is on the Projects page.", ToastKind.Success);
    }
}
