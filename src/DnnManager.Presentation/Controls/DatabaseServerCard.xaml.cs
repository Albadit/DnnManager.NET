using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The server new projects get their database on (Settings → Database server), tested with Test: it answers, the
/// sign-in works, its version, and whether the login may create databases. In Settings → Database server; it works
/// with the saved settings.
/// </summary>
public partial class DatabaseServerCard : UserControl
{
    private AppOptions _options = null!;
    private ISecretStore _secrets = null!;
    private IDatabaseProvisioner _databases = null!;
    // Which Test the shown result belongs to - an answer for an older one, or for settings saved since, is dropped.
    private int _version;

    public DatabaseServerCard()
    {
        InitializeComponent();
        // Saved settings can name another server - only while shown, so a closed Settings page isn't kept alive.
        Loaded += (_, _) =>
        {
            if (_options is null) return;
            _options.Changed += OnSettingsChanged;
            ShowSummary();
        };
        Unloaded += (_, _) =>
        {
            if (_options is not null) _options.Changed -= OnSettingsChanged;
        };
    }

    public void Attach(IServiceProvider services)
    {
        _options = services.GetRequiredService<IOptions<AppOptions>>().Value;
        _secrets = services.GetRequiredService<ISecretStore>();
        _databases = services.GetRequiredService<IDatabaseProvisioner>();
        ShowSummary();
    }

    private void OnSettingsChanged()
    {
        // What was shown belongs to the server as it was.
        _version++;
        CheckList.Show(null);
        TestButton.IsEnabled = true;
        TestButton.Content = "Test";
        ShowSummary();
    }

    /// <summary>The server in the settings, in words - never the password.</summary>
    private void ShowSummary()
    {
        var server = _options.DatabaseServer;
        var connection = _options.DatabaseOnServer("");
        Summary.Text = server.IsContainer ? $"Local SQL container (Docker) at {connection.Server}, as sa."
            : server.IsLocalDbFile ? $"SQL Server Express LocalDB ({connection.Server}) - each site's own database file."
            : $"{connection.Server}, with {connection.AuthenticationText.ToLowerInvariant()}" +
              (server.UsesSqlAuthentication ? $" as '{server.UserName}'." : ".");
    }

    private void Test_Click(object sender, RoutedEventArgs e) => Test();

    /// <summary>Tests the server in the settings.</summary>
    public async void Test()
    {
        var version = ++_version;
        var password = _options.DatabaseServer.UsesSqlAuthentication ? _secrets.Read(SecretNames.DatabaseServerPassword) ?? "" : "";
        var connection = _options.DatabaseOnServer("", password);
        TestButton.IsEnabled = false;
        TestButton.Content = "Testing…";
        CheckList.ShowTesting();
        try
        {
            var report = await _databases.CheckAsync(connection, new DatabaseCheckOptions(ForNewInstall: true, ServerOnly: true),
                CancellationToken.None);
            if (version == _version) CheckList.Show(report);
        }
        finally
        {
            if (version == _version)
            {
                TestButton.IsEnabled = true;
                TestButton.Content = "Test";
            }
        }
    }
}
