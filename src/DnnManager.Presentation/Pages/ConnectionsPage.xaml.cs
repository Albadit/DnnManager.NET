using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages;

/// <summary>"Live sites": the per-project FTP/SQL connections of live websites, in connections.json.</summary>
public partial class ConnectionsPage : UserControl
{
    private enum StatusKind { Info, Success, Error }

    private readonly IFtpProfileStore _ftp;
    private readonly ISqlProfileStore _sql;
    private readonly IFtpBrowser _ftpBrowser;
    private readonly ISqlConnectionTester _sqlTester;
    private readonly ObservableCollection<string> _projects;

    public ConnectionsPage(IFtpProfileStore ftp, ISqlProfileStore sql, IFtpBrowser ftpBrowser, ISqlConnectionTester sqlTester)
    {
        _ftp = ftp; _sql = sql; _ftpBrowser = ftpBrowser; _sqlTester = sqlTester;
        InitializeComponent();

        _projects = new ObservableCollection<string>(_ftp.ListProjects()
            .Concat(_sql.ListProjects())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        ProjectList.ItemsSource = _projects;
        UpdateEmptyState();
        if (_projects.Count > 0) ProjectList.SelectedIndex = 0;
    }

    private string? Project => ProjectList.SelectedItem as string;

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = _projects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Editor.IsEnabled = Project is not null;
        FtpStatus.Text = SqlStatus.Text = "";
        if (Project is not { } project) return;

        var ftp = _ftp.Get(project);
        FtpHost.Text = ftp?.Host ?? "";
        FtpPort.Text = ftp?.Port.ToString() ?? "21";
        FtpUser.Text = ftp?.User ?? "";
        FtpPassword.Password = ftp is null ? "" : _ftp.Unprotect(ftp.EncryptedPassword);
        FtpRemote.Text = ftp?.RemotePath ?? "/";

        var sql = _sql.Get(project);
        SqlServer.Text = sql?.Server ?? "";
        SqlDatabase.Text = sql?.Database ?? "";
        SqlUser.Text = sql?.User ?? "";
        SqlPassword.Password = sql is null ? "" : _sql.Unprotect(sql.EncryptedPassword);

        if (ftp is null && sql is null)
            Status(FtpStatus, "New live site - fill in its FTP and/or SQL connection and save.", StatusKind.Info);
    }

    // ─── ADD ──────────────────────────────────────────────────────────────

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Connections are keyed by project name, and cloning reuses them for the project of that name,
        // so the name has to be one a project can actually have.
        var initial = "";
        while (true)
        {
            var name = InputDialog.Show("Name of the live site (also the local project name when cloned)", initial)?.Trim();
            if (name is null) return;

            var check = ProjectName.Validate(name);
            if (!check.Success)
            {
                Dialogs.Error(check.Error!);
                initial = name;
                continue;
            }

            var existing = _projects.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = name;
                var index = 0;
                while (index < _projects.Count && StringComparer.OrdinalIgnoreCase.Compare(_projects[index], name) < 0) index++;
                _projects.Insert(index, name);
                UpdateEmptyState();
            }
            ProjectList.SelectedItem = existing;
            ProjectList.ScrollIntoView(existing);
            FtpHost.Focus();
            return;
        }
    }

    // ─── FTP ──────────────────────────────────────────────────────────────

    private void SaveFtp_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project) return;
        var host = FtpHost.Text.Trim();
        if (host.Length == 0) { Status(FtpStatus, "Enter the FTP host.", StatusKind.Error); return; }

        var port = int.TryParse(FtpPort.Text, out var p) ? p : 21;
        var user = FtpUser.Text.Trim();
        var remote = string.IsNullOrWhiteSpace(FtpRemote.Text) ? "/" : FtpRemote.Text.Trim();

        var name = user.Length == 0 ? host : $"{user}@{host}";
        _ftp.Save(project, new FtpProfile(name, host, port, user, _ftp.Protect(FtpPassword.Password), remote));
        Status(FtpStatus, $"FTP connection for '{project}' saved.", StatusKind.Success);
    }

    private async void TestFtp_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project) return;
        var host = FtpHost.Text.Trim();
        if (host.Length == 0) { Status(FtpStatus, "Enter the FTP host.", StatusKind.Error); return; }

        var port = int.TryParse(FtpPort.Text, out var p) ? p : 21;
        var user = FtpUser.Text.Trim();
        var remote = string.IsNullOrWhiteSpace(FtpRemote.Text) ? "/" : FtpRemote.Text.Trim();
        var password = FtpPassword.Password;

        TestFtpButton.IsEnabled = false;
        Status(FtpStatus, $"Connecting to {host}:{port} …", StatusKind.Info);
        try
        {
            // Listing the remote path proves the login and that the folder is there.
            var listing = await _ftpBrowser.ListDirectoriesAsync(host, port, user, password, remote, CancellationToken.None);
            if (Project != project) return; // switched to another project meanwhile
            if (listing.Success)
                Status(FtpStatus, $"✓ Connected to {host} - {remote} has {listing.Value!.Count} folder(s).", StatusKind.Success);
            else
                Status(FtpStatus, $"✗ {listing.Error}", StatusKind.Error);
        }
        finally
        {
            TestFtpButton.IsEnabled = true;
        }
    }

    // ─── SQL ──────────────────────────────────────────────────────────────

    private void SaveSql_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project) return;
        var server = SqlServer.Text.Trim();
        var database = SqlDatabase.Text.Trim();
        var user = SqlUser.Text.Trim();
        if (server.Length == 0 || database.Length == 0 || user.Length == 0)
        {
            Status(SqlStatus, "Server, database and user are required.", StatusKind.Error);
            return;
        }

        _sql.Save(project, new SqlProfile($"{user}@{server}", server, database, user, _sql.Protect(SqlPassword.Password)));
        Status(SqlStatus, $"SQL connection for '{project}' saved.", StatusKind.Success);
    }

    private async void TestSql_Click(object sender, RoutedEventArgs e)
    {
        if (Project is not { } project) return;
        var server = SqlServer.Text.Trim();
        var database = SqlDatabase.Text.Trim();
        var user = SqlUser.Text.Trim();
        if (server.Length == 0 || database.Length == 0 || user.Length == 0)
        {
            Status(SqlStatus, "Server, database and user are required.", StatusKind.Error);
            return;
        }
        var password = SqlPassword.Password;

        TestSqlButton.IsEnabled = false;
        Status(SqlStatus, $"Connecting to {server} …", StatusKind.Info);
        try
        {
            var result = await _sqlTester.TestAsync(new SiteSqlConnection(server, database, user, password), CancellationToken.None);
            if (Project != project) return; // switched to another project meanwhile
            if (result.Success)
                Status(SqlStatus, $"✓ Connected to {result.Value}.", StatusKind.Success);
            else
                Status(SqlStatus, $"✗ {result.Error}", StatusKind.Error);
        }
        finally
        {
            TestSqlButton.IsEnabled = true;
        }
    }

    private void Status(TextBlock target, string text, StatusKind kind)
    {
        target.Text = text;
        target.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Success => "SuccessText",
            StatusKind.Error   => "ErrorText",
            _                  => "TextMuted",
        });
    }
}
