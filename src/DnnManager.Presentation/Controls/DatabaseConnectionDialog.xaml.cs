using System.IO;
using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Where a site's web.config points SiteSqlServer: the connection type, server, sign-in and database, with the same
/// fields as the Database card of New project. Nothing in a database is moved or copied - only the connection changes,
/// so it is tested here (not required) before it is saved.
/// </summary>
public partial class DatabaseConnectionDialog : Window
{
    private readonly DatabaseConnection? _current;
    private readonly string _siteLogin;
    private readonly AppOptions _options;
    private readonly ISecretStore _secrets;
    private readonly IDatabaseProvisioner _databases;

    // The connection type shown.
    private DatabaseKind _type;
    // The database box is shared: the container and SQL Server have a database name, LocalDB the .mdf file in App_Data -
    // each is kept while the other is shown, so switching back and forth loses nothing.
    private string _databaseName, _localDbFile;
    // LocalDB isn't asked for its instance; a site already on another one keeps it.
    private readonly string _localDbServer;
    // The SQL Server fields are filled in the first time SQL Server is chosen - from the site, else from the settings.
    private bool _sqlServerFilled;
    // Set while code fills fields in, so that isn't taken for an edit.
    private bool _filling;

    // The last test: the values it ran with and what it found, so its result is shown only while the fields still say that.
    private bool _testing;
    private DatabaseConnection? _tested;
    private DatabaseCheckReport? _report;

    private DatabaseConnectionDialog(IServiceProvider services, string site, DatabaseConnection? current, string siteLogin)
    {
        _current = current;
        _siteLogin = siteLogin;
        _options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        _secrets = services.GetRequiredService<ISecretStore>();
        _databases = services.GetRequiredService<IDatabaseProvisioner>();
        InitializeComponent();
        ThemeManager.Track(this);
        Intro.Text = $"Where '{site}' finds its database - the SiteSqlServer connection string in its web.config. The site is " +
                     "restarted after saving. Nothing in the databases is moved or copied: point it at a database that holds " +
                     "this site, or restore a backup there first.";

        // A site without a connection starts on the local container, with a database named like the site.
        _type = current?.Kind ?? DatabaseKind.Container;
        _databaseName = current is { Kind: not DatabaseKind.LocalDbFile } ? current.Database : site;
        _localDbFile = current is { Kind: DatabaseKind.LocalDbFile, Database.Length: > 0 } ? current.Database : DatabaseConnection.LocalDbFileName;
        _localDbServer = current is { Kind: DatabaseKind.LocalDbFile, Server.Length: > 0 } ? current.Server : DatabaseConnection.LocalDbServer;

        _filling = true;
        DbContainer.IsChecked = _type == DatabaseKind.Container;
        DbSqlServer.IsChecked = _type == DatabaseKind.SqlServer;
        DbLocalDb.IsChecked = _type == DatabaseKind.LocalDbFile;
        DbNameBox.Text = _type == DatabaseKind.LocalDbFile ? _localDbFile : _databaseName;
        if (_type == DatabaseKind.SqlServer) FillSqlServer();
        _filling = false;
        UpdateState();
        Loaded += (_, _) => (_type == DatabaseKind.SqlServer ? DbServerBox : DbNameBox).Focus();
    }

    /// <summary>The new connection, or null when cancelled.</summary>
    public static DatabaseConnection? Show(IServiceProvider services, string site, DatabaseConnection? current, string siteLogin)
    {
        var dialog = new DatabaseConnectionDialog(services, site, current, siteLogin)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.ChosenConnection() : null;
    }

    private string ContainerServer => _options.ServerFor(_options.Docker.DefaultPort);

    /// <summary>
    /// The SQL Server fields: the site's own when it is on SQL Server, otherwise Settings → Database server's when that
    /// is a SQL Server (the login's password from the Credential Manager, held in memory only).
    /// </summary>
    private void FillSqlServer()
    {
        _sqlServerFilled = true;
        var filling = _filling;
        _filling = true;
        if (_current is { Kind: DatabaseKind.SqlServer } current)
        {
            DbServerBox.Text = current.Server;
            DbWindowsAuth.IsChecked = current.Authentication == SqlAuthentication.Windows;
            DbSqlAuth.IsChecked = current.Authentication == SqlAuthentication.Sql;
            DbUserBox.Text = current.User;
            DbPasswordBox.Password = current.Password;
        }
        else
        {
            var server = _options.DatabaseServer;
            var saved = !server.IsContainer && !server.IsLocalDbFile;
            DbServerBox.Text = saved && server.Server.Length > 0 ? server.Server : @".\SQLEXPRESS";
            DbWindowsAuth.IsChecked = !server.UsesSqlAuthentication;
            DbSqlAuth.IsChecked = server.UsesSqlAuthentication;
            DbUserBox.Text = saved ? server.UserName : "";
            DbPasswordBox.Password = server.UsesSqlAuthentication ? _secrets.Read(SecretNames.DatabaseServerPassword) ?? "" : "";
        }
        _filling = filling;
    }

    private void DbType_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _filling) return;
        var type = DbSqlServer.IsChecked == true ? DatabaseKind.SqlServer
            : DbLocalDb.IsChecked == true ? DatabaseKind.LocalDbFile
            : DatabaseKind.Container;
        if (type == _type) return;
        _filling = true;
        // The database box changes meaning between a database name and LocalDB's file.
        if (_type == DatabaseKind.LocalDbFile)
        {
            _localDbFile = DbNameBox.Text.Trim();
            DbNameBox.Text = _databaseName;
        }
        else if (type == DatabaseKind.LocalDbFile)
        {
            _databaseName = DbNameBox.Text.Trim();
            DbNameBox.Text = _localDbFile;
        }
        if (type == DatabaseKind.SqlServer && !_sqlServerFilled) FillSqlServer();
        _type = type;
        _filling = false;
        UpdateState();
    }

    private void DbAuth_Checked(object sender, RoutedEventArgs e) => FieldChanged();

    private void DbField_Changed(object sender, TextChangedEventArgs e) => FieldChanged();

    private void DbPassword_Changed(object sender, RoutedEventArgs e) => FieldChanged();

    private void FieldChanged()
    {
        if (IsInitialized && !_filling) UpdateState();
    }

    private bool SqlLogin => _type == DatabaseKind.SqlServer && DbSqlAuth.IsChecked == true;

    /// <summary>What's wrong with the fields, or null when they make a connection.</summary>
    private string? FieldProblem()
    {
        var name = DbNameBox.Text.Trim();
        if (_type == DatabaseKind.LocalDbFile)
        {
            if (name.Length == 0) return "Enter the database file's name.";
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase)
                ? $"Enter the name of a .mdf file in the site's App_Data folder, e.g. {DatabaseConnection.LocalDbFileName}."
                : null;
        }
        if (_type == DatabaseKind.SqlServer && DbServerBox.Text.Trim().Length == 0) return "Enter the server.";
        if (name.Length == 0) return "Enter the database name.";
        if (name.Length > 128 || name.Contains(']')) return "The database name can have at most 128 characters, and no ']'.";
        if (SqlLogin && DbUserBox.Text.Trim().Length == 0) return "Enter the username.";
        if (SqlLogin && DbPasswordBox.Password.Length == 0) return "Enter the password.";
        return null;
    }

    /// <summary>The connection the fields describe - the container's server and login from the settings.</summary>
    private DatabaseConnection ChosenConnection()
    {
        var database = DbNameBox.Text.Trim();
        return _type switch
        {
            DatabaseKind.Container => new DatabaseConnection(DatabaseKind.Container, ContainerServer, database, SqlAuthentication.Sql,
                _options.Docker.SqlUser, _options.Docker.SaPassword),
            DatabaseKind.LocalDbFile => new DatabaseConnection(DatabaseKind.LocalDbFile, _localDbServer, database, SqlAuthentication.Windows),
            _ => SqlLogin
                ? new DatabaseConnection(DatabaseKind.SqlServer, DbServerBox.Text.Trim(), database, SqlAuthentication.Sql, DbUserBox.Text.Trim(), DbPasswordBox.Password)
                : new DatabaseConnection(DatabaseKind.SqlServer, DbServerBox.Text.Trim(), database, SqlAuthentication.Windows)
        };
    }

    /// <summary>
    /// Whether two connections reach the same database the same way. A user name and password only count with SQL Server
    /// authentication - with Windows authentication they aren't used.
    /// </summary>
    private static bool Same(DatabaseConnection a, DatabaseConnection b) =>
        a.Kind == b.Kind
        && string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Database, b.Database, StringComparison.OrdinalIgnoreCase)
        && a.UsesWindowsAuthentication == b.UsesWindowsAuthentication
        && (a.UsesWindowsAuthentication || (a.User == b.User && a.Password == b.Password));

    private void UpdateState()
    {
        if (!IsInitialized || OkButton is null) return;
        DbServerPanel.Visibility = DbAuthPanel.Visibility = _type == DatabaseKind.SqlServer ? Visibility.Visible : Visibility.Collapsed;
        DbUserPanel.Visibility = DbPasswordPanel.Visibility = SqlLogin ? Visibility.Visible : Visibility.Collapsed;
        DbNameLabel.Text = _type == DatabaseKind.LocalDbFile ? "Database file in App_Data" : "Database name";
        // The name box sits on its own row when it is the only field.
        Grid.SetColumn(DbNamePanel, _type == DatabaseKind.SqlServer ? 2 : 0);
        Grid.SetColumnSpan(DbNamePanel, _type == DatabaseKind.SqlServer ? 1 : 3);

        var login = _siteLogin.Length > 0 ? $"its app pool's identity ({_siteLogin})" : "its app pool's identity";
        DatabaseHint.Text = _type switch
        {
            DatabaseKind.Container => $"On the local SQL container at {ContainerServer}, signed in to as '{_options.Docker.SqlUser}' - " +
                                      "as Settings → Database server has it.",
            DatabaseKind.LocalDbFile => $"The site's own App_Data\\{(DbNameBox.Text.Trim().Length > 0 ? DbNameBox.Text.Trim() : DatabaseConnection.LocalDbFileName)}, " +
                                        $"attached by LocalDB ({_localDbServer}) - the site signs in as {login}.",
            _ => SqlLogin ? "The site signs in with this SQL Server login - its password is written to web.config."
                : $"The site signs in as {login}."
        };

        var problem = FieldProblem();
        var connection = problem is null ? ChosenConnection() : null;
        TestButton.IsEnabled = connection is not null && !_testing;
        // A test's result belongs to the values it ran with - another value hides it.
        var testedNow = connection is not null && _tested is not null && Same(connection, _tested);
        if (!_testing) CheckList.Show(testedNow ? _report : null);

        // Testing isn't required, but saving what just failed is worth a second look.
        var shown = problem ?? (testedNow && _report is { Passed: false } ? "The last test failed - save anyway?" : null);
        Problem.Text = shown ?? "";
        Problem.Visibility = shown is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = connection is not null && (_current is null || !Same(connection, _current));
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (FieldProblem() is not null || _testing) return;
        var connection = ChosenConnection();
        _testing = true;
        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        CheckList.ShowTesting();
        try
        {
            // With Windows authentication the site signs in as its app pool: that login is checked too.
            var report = await _databases.CheckAsync(connection,
                new DatabaseCheckOptions(ForNewInstall: false, SiteLogin: connection.UsesWindowsAuthentication && _siteLogin.Length > 0 ? _siteLogin : null),
                CancellationToken.None);
            _tested = connection;
            _report = report;
        }
        finally
        {
            _testing = false;
            TestButton.Content = "Test connection";
            UpdateState();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FieldProblem() is not null) return;
        DialogResult = true;
    }
}
