using Avalonia.Controls;
using Avalonia.Interactivity;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>
/// The server new projects get their database on (Settings → Database server), tested with Test: it answers, the
/// sign-in works, its version, and whether the login may create databases - the Windows app's DatabaseServerCard. It
/// works with the saved settings; <see cref="Attach"/> gives it the app's services.
/// </summary>
public partial class DatabaseServerCard : UserControl
{
    private AppOptions? _options;
    private ISecretStore _secrets = null!;
    private IDatabaseProvisioner _databases = null!;
    // Which Test the shown result belongs to - an answer for an older one, or for settings saved since, is dropped.
    private int _version;

    public DatabaseServerCard()
    {
        InitializeComponent();
        // Saved settings can name another server - followed only while shown, so a closed page isn't kept alive.
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

    // The settings change on the thread that saves them: shown on the UI thread.
    private void OnSettingsChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        // What was shown belongs to the server as it was.
        _version++;
        CheckList.Show(null);
        TestButton.IsEnabled = true;
        TestButton.Content = "Test";
        ShowSummary();
    });

    /// <summary>The server in the settings, in words - never the password.</summary>
    private void ShowSummary()
    {
        if (_options is null) return;
        var server = _options.DatabaseServer;
        var connection = _options.DatabaseOnServer("");
        Summary.Text = server.IsContainer ? $"Local SQL container (Docker) at {connection.Server}, as {connection.User}."
            : server.IsLocalDbFile ? $"SQL Server Express LocalDB ({connection.Server}) - each site's own database file."
            : $"{connection.Server}, with {connection.AuthenticationText.ToLowerInvariant()}" +
              (server.UsesSqlAuthentication ? $" as '{server.UserName}'." : ".");
    }

    private void Test_Click(object? sender, RoutedEventArgs e) => Test();

    /// <summary>Tests the server in the settings.</summary>
    public async void Test()
    {
        if (_options is null) return;
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
        catch (Exception ex)
        {
            // The Windows app's async void lets this end the app; here it is said, and the button comes back.
            if (version == _version)
            {
                CheckList.Show(null);
                await Services.Dialogs.MessageAsync($"Could not test the database server: {ex.Message}");
            }
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
