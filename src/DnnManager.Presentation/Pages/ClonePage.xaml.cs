using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>"Clone a DNN project (local / FTP)".</summary>
public partial class ClonePage : UserControl, IRefreshable
{
    private readonly OperationRunner _runner;
    private readonly IFtpBrowser _ftp;
    private readonly IFtpProfileStore _ftpProfiles;
    private readonly ISqlProfileStore _sqlProfiles;
    private readonly AppOptions _options;
    private readonly bool _ready;

    // Project == null means "[ New project ]".
    public sealed record SavedPick(string? Project, string Label);
    private sealed record FtpConnection(string Host, int Port, string User, string Password);

    // Set once "Connect & browse" succeeds; any edit to the connection fields clears it.
    private FtpConnection? _connection;
    private string _cwd = "/";
    // The saved FTP profile the connection fields were filled from (browsing starts in its folder).
    private FtpProfile? _prefill;
    private string? _prefillFor;

    public ClonePage(OperationRunner runner, IFtpBrowser ftp, IFtpProfileStore ftpProfiles,
        ISqlProfileStore sqlProfiles, IOptions<AppOptions> options)
    {
        _runner = runner; _ftp = ftp; _ftpProfiles = ftpProfiles; _sqlProfiles = sqlProfiles;
        _options = options.Value;
        InitializeComponent();
        LoadLists();
        _ready = true;
        UpdateState();
    }

    public void Refresh()
    {
        LoadLists();
        UpdateState();
    }

    private void LoadLists()
    {
        var selectedSaved = (SavedProjectCombo.SelectedItem as SavedPick)?.Project;
        var picks = _ftpProfiles.ListProjects()
            .Select(p => new SavedPick(p, p))
            .Append(new SavedPick(null, "[ New project ]"))
            .ToList();
        SavedProjectCombo.ItemsSource = picks;
        SavedProjectCombo.SelectedItem = picks.FirstOrDefault(p => p.Project == selectedSaved) ?? picks[0];

        var selectedLocal = LocalSourceCombo.SelectedItem as string;
        var parent = _options.BaseDirectory;
        var subs = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string?>();
        LocalSourceCombo.ItemsSource = subs;
        LocalSourceCombo.SelectedItem = subs.FirstOrDefault(s => s == selectedLocal);
        LocalHint.Text = !Directory.Exists(parent) ? $"Base folder does not exist: {parent}"
            : subs.Count == 0 ? $"No subfolders found in {parent}."
            : $"Projects in {parent}.";
    }

    private bool IsFtp => FtpRadio.IsChecked == true;

    // An already-saved FTP project: its connections are reused and only the action is chosen.
    private string? SavedProject => IsFtp ? (SavedProjectCombo.SelectedItem as SavedPick)?.Project : null;

    private string TargetName => SavedProject ?? NameBox.Text.Trim();

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateState();
    }

    private void UpdateState()
    {
        var saved = SavedProject;
        var isNew = saved is null;

        SavedProjectPanel.Visibility = IsFtp ? Visibility.Visible : Visibility.Collapsed;
        NamePanel.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        ActionPanel.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        LocalCard.Visibility = IsFtp ? Visibility.Collapsed : Visibility.Visible;
        FtpCard.Visibility = IsFtp && isNew ? Visibility.Visible : Visibility.Collapsed;
        SqlCard.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;

        var name = TargetName;
        var nameCheck = ProjectName.Validate(name);
        NameError.Text = isNew && name.Length > 0 && !nameCheck.Success ? nameCheck.Error ?? "" : "";
        NameError.Visibility = NameError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!isNew) ShowSavedSummary(saved!);
        else if (nameCheck.Success)
        {
            if (IsFtp) PrefillFtp(name);
            UpdateSavedSqlOption(name);
        }
        else UpdateSavedSqlOption(null);

        SqlNewPanel.Visibility = SqlNew.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var missing = MissingInput(nameCheck.Success);
        RunButton.IsEnabled = missing is null;
        RunHint.Text = missing ?? "";
    }

    /// <summary>What still has to be filled in before the clone can start, or null when ready.</summary>
    private string? MissingInput(bool nameValid)
    {
        if (!nameValid) return TargetName.Length == 0 ? "Enter a project name." : null;
        if (SavedProject is not null) return null;
        if (!IsFtp && LocalSourceCombo.SelectedItem is not string) return "Choose the source project.";
        if (IsFtp && _connection is null) return "Connect to the FTP server and pick the folder to clone.";
        if (SqlNew.IsChecked == true &&
            (SqlServer.Text.Trim().Length == 0 || SqlDatabase.Text.Trim().Length == 0 || SqlUser.Text.Trim().Length == 0))
            return "Fill in the SQL server, database and user.";
        return null;
    }

    private void ShowSavedSummary(string project)
    {
        var ftp = _ftpProfiles.Get(project);
        var sql = _sqlProfiles.Get(project);
        SavedSummary.Text =
            (ftp is null ? "FTP: (none)" : $"FTP: {ftp.User}@{ftp.Host}:{ftp.Port}  {ftp.RemotePath}") + Environment.NewLine +
            (sql is null ? "SQL: from web.config" : $"SQL: {sql.User}@{sql.Server}/{sql.Database}");
    }

    private void UpdateSavedSqlOption(string? project)
    {
        var saved = project is null ? null : _sqlProfiles.Get(project);
        if (saved is null)
        {
            SqlSaved.Visibility = Visibility.Collapsed;
            if (SqlSaved.IsChecked == true) SqlWebConfig.IsChecked = true;
            return;
        }
        var wasHidden = SqlSaved.Visibility != Visibility.Visible;
        SqlSaved.Content = $"Use saved  ({saved.User}@{saved.Server}/{saved.Database})";
        SqlSaved.Visibility = Visibility.Visible;
        if (wasHidden) SqlSaved.IsChecked = true;
    }

    // ─── FTP ──────────────────────────────────────────────────────────────

    /// <summary>A new project whose name already has a saved FTP connection starts from that connection.</summary>
    private void PrefillFtp(string project)
    {
        if (string.Equals(_prefillFor, project, StringComparison.OrdinalIgnoreCase)) return;
        var saved = _ftpProfiles.Get(project);
        if (saved is null) return;

        // Recorded first: filling the fields raises their change events, which re-enter UpdateState.
        _prefill = saved;
        _prefillFor = project;
        FtpHost.Text = saved.Host;
        FtpPort.Text = saved.Port.ToString();
        FtpUser.Text = saved.User;
        FtpPassword.Password = _ftpProfiles.Unprotect(saved.EncryptedPassword);
    }

    private void FtpField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _connection = null;
        BrowserPanel.Visibility = Visibility.Collapsed;
        FtpStatus.Text = "";
        UpdateState();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = FtpHost.Text.Trim();
        if (host.Length == 0) { SetFtpStatus("Enter the FTP host.", error: true); return; }
        var port = int.TryParse(FtpPort.Text, out var p) ? p : 21;
        var user = FtpUser.Text.Trim();
        var password = FtpPassword.Password;

        // Still on the saved server/user: start browsing in the saved folder.
        var start = "/";
        if (_prefill is not null && string.Equals(_prefill.Host, host, StringComparison.OrdinalIgnoreCase) && _prefill.User == user
            && !string.IsNullOrWhiteSpace(_prefill.RemotePath))
            start = _prefill.RemotePath;

        var connection = new FtpConnection(host, port, user, password);
        SetFtpStatus($"Connecting to {host}:{port} …", error: false);
        if (await BrowseAsync(connection, start))
            SetFtpStatus($"Connected to {host}.", error: false);
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is not null) await BrowseAsync(_connection, ParentPath(_cwd));
    }

    private async void FtpDirs_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_connection is not null && FtpDirs.SelectedItem is string dir)
            await BrowseAsync(_connection, JoinPath(_cwd, dir));
    }

    private async Task<bool> BrowseAsync(FtpConnection connection, string path)
    {
        FtpCard.IsEnabled = false;
        try
        {
            var listing = await _ftp.ListDirectoriesAsync(connection.Host, connection.Port, connection.User,
                connection.Password, path, CancellationToken.None);
            if (!listing.Success || listing.Value is null)
            {
                SetFtpStatus(listing.Error ?? "Failed to list FTP folder.", error: true);
                return false;
            }

            _connection = connection;
            _cwd = path;
            CwdText.Text = $"{connection.Host}:{path}";
            UpButton.IsEnabled = path != "/";
            FtpDirs.ItemsSource = listing.Value;
            BrowserPanel.Visibility = Visibility.Visible;
            return true;
        }
        finally
        {
            FtpCard.IsEnabled = true;
            UpdateState();
        }
    }

    private void SetFtpStatus(string text, bool error)
    {
        FtpStatus.Text = text;
        FtpStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorText" : "TextMuted");
    }

    private static string JoinPath(string cwd, string name)
        => cwd == "/" ? "/" + name : cwd.TrimEnd('/') + "/" + name;

    private static string ParentPath(string cwd)
    {
        if (cwd == "/" || string.IsNullOrEmpty(cwd)) return "/";
        var trimmed = cwd.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        if (idx <= 0) return "/";
        return trimmed[..idx];
    }

    // ─── RUN ──────────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetName;
        if (!ProjectName.Validate(target).Success || MissingInput(true) is not null) return;

        // Everything the operation needs is read from the controls here, on the UI thread.
        var saved = SavedProject;
        var copyFiles = saved is null || ActionFull.IsChecked == true || ActionFiles.IsChecked == true;
        var seedDatabase = saved is null || ActionFull.IsChecked == true || ActionDb.IsChecked == true;
        var local = !IsFtp ? Path.Combine(_options.BaseDirectory, (string)LocalSourceCombo.SelectedItem) : null;
        var ftp = _connection;
        var remotePath = _cwd;
        var sqlMode = SqlSaved.IsChecked == true ? "saved" : SqlNew.IsChecked == true ? "new" : "webconfig";
        var newSql = (Host: SqlServer.Text.Trim(), Port: int.TryParse(SqlPort.Text, out var sp) ? sp : 1433,
                      Database: SqlDatabase.Text.Trim(), User: SqlUser.Text.Trim(), Password: SqlPassword.Password);

        // Backup destination for the source DB (always auto-generated).
        var bakPath = Path.Combine(Path.GetTempPath(), $"dnnmgr_clone_{target}_{DateTime.Now:yyyyMMddHHmmss}.bak");

        await _runner.RunAsync($"Clone → '{target}'", async (services, reporter, ct) =>
        {
            CloneSource source;
            SiteSqlConnection? dbOverride;

            if (saved is not null)
            {
                var profile = _ftpProfiles.Get(saved);
                if (profile is null) return Result.Fail($"No saved FTP connection for '{saved}'.");
                source = new CloneSource(CloneSourceKind.Ftp, null, profile.Host, profile.Port, profile.User,
                    _ftpProfiles.Unprotect(profile.EncryptedPassword), profile.RemotePath);
                var savedSql = _sqlProfiles.Get(saved);
                dbOverride = savedSql is null ? null
                    : new SiteSqlConnection(savedSql.Server, savedSql.Database, savedSql.User, _sqlProfiles.Unprotect(savedSql.EncryptedPassword));
            }
            else
            {
                if (ftp is not null && local is null)
                {
                    // Persist the connection (with the chosen remote directory) under this project.
                    var profileName = string.IsNullOrWhiteSpace(ftp.User) ? ftp.Host : $"{ftp.User}@{ftp.Host}";
                    _ftpProfiles.Save(target, new FtpProfile(profileName, ftp.Host, ftp.Port, ftp.User,
                        _ftpProfiles.Protect(ftp.Password), remotePath));
                    reporter.Success($"Saved FTP profile for '{target}' ({remotePath}).");
                    source = new CloneSource(CloneSourceKind.Ftp, null, ftp.Host, ftp.Port, ftp.User, ftp.Password, remotePath);
                }
                else
                {
                    source = new CloneSource(CloneSourceKind.LocalFolder, local, null, 0, null, null, null);
                }

                switch (sqlMode)
                {
                    case "saved":
                        var p = _sqlProfiles.Get(target)!;
                        dbOverride = new SiteSqlConnection(p.Server, p.Database, p.User, _sqlProfiles.Unprotect(p.EncryptedPassword));
                        break;
                    case "new":
                        var server = $"{newSql.Host},{newSql.Port}";
                        _sqlProfiles.Save(target, new SqlProfile($"{newSql.User}@{server}", server, newSql.Database,
                            newSql.User, _sqlProfiles.Protect(newSql.Password)));
                        reporter.Success($"Saved SQL profile for '{target}'.");
                        dbOverride = new SiteSqlConnection(server, newSql.Database, newSql.User, newSql.Password);
                        break;
                    default:
                        dbOverride = null; // use whatever web.config already has
                        break;
                }
            }

            var req = new CloneProjectRequest
            {
                TargetProjectName = target,
                Source = source,
                SourceBackupServerPath = bakPath,
                CreateIisSite = true,
                SourceDbOverride = dbOverride,
                CopyFiles = copyFiles,
                SeedDatabase = seedDatabase
            };
            return await services.GetRequiredService<CloneProjectUseCase>().ExecuteAsync(req, reporter, ct);
        });
    }
}
