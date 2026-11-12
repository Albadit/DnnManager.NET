using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DnnManager.Application.Deployment;
using DnnManager.Desktop.Services;
using DnnManager.Domain;

namespace DnnManager.Desktop.Views;

/// <summary>
/// Adds or edits a project's server - as the Windows app's connection dialog: where it is, how to sign in, its folder,
/// the folder on this computer it is compared with, the files left out. Checked before it is saved; the server's
/// identity (an SSH host key, a TLS certificate) is trusted only once the user has seen it - Test connection, or Save.
/// </summary>
public partial class ServerDialog : Window
{
    private static readonly (DeployAuth Auth, string Text)[] Logons =
        [(DeployAuth.Anonymous, "Anonymous"), (DeployAuth.Password, "Normal"), (DeployAuth.AskForPassword, "Ask for password"), (DeployAuth.PrivateKey, "Key file")];

    private static readonly string[] Encryptions =
        ["Use explicit FTP over TLS if available", "Require explicit FTP over TLS", "Require implicit FTP over TLS", "Only use plain FTP (insecure)"];

    private readonly DeployConnections _connections = null!;
    private readonly DeployConnection? _existing;
    private readonly DeploySecrets _saved = DeploySecrets.None;
    private readonly string _projectsFolder = "";
    private string? _trusted;
    private string? _key;
    private CancellationTokenSource? _test;
    private DeployConnection? _result;

    public ServerDialog() => InitializeComponent();

    internal ServerDialog(DeployConnections connections, string projectsFolder, IReadOnlyList<string> projects, DeployConnection? existing) : this()
    {
        _connections = connections;
        _existing = existing;
        _projectsFolder = projectsFolder;
        _saved = existing is null ? DeploySecrets.None : connections.Secrets(existing.Id);
        _trusted = existing?.TrustedHostKey;
        Title = existing is null ? "Add a server" : $"Edit '{existing.Name}' of '{existing.Site}'";
        ProjectBox.ItemsSource = projects;
        ProjectBox.Text = existing?.Site ?? (projects.Count == 1 ? projects[0] : "");
        ProjectBox.IsEnabled = existing is null;
        // Its text as it is set - typed or given.
        ProjectBox.PropertyChanged += (_, e) => { if (e.Property == AutoCompleteBox.TextProperty) ShowLocalHint(); };
        EnvironmentBox.ItemsSource = Enum.GetValues<DeployEnvironment>();
        EncryptionBox.ItemsSource = Encryptions;

        var c = existing ?? connections.New(ProjectBox.Text ?? "");
        NameBox.Text = c.Name;
        EnvironmentBox.SelectedItem = c.Environment;
        ProtocolBox.SelectedIndex = c.Protocol == TransferProtocol.Sftp ? 0 : 1;
        EncryptionBox.SelectedIndex = (int)c.Encryption;
        HostBox.Text = c.Host;
        // The usual port is shown as nothing, as FileZilla shows it.
        PortBox.Text = existing is null || c.Port == DeployConnection.DefaultPort(c.Protocol, c.Encryption) ? "" : c.Port.ToString();
        FillLogons(c.Auth);
        UserBox.Text = c.UserName;
        RootBox.Text = c.RemoteRoot;
        LocalBox.Text = c.LocalRoot ?? "";
        LocalBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) ShowLocalHint(); };
        RulesBox.Text = c.IgnoreRules;
        if (!string.IsNullOrEmpty(_saved.PrivateKey)) KeyBox.PlaceholderText = "A key is saved - Browse… to replace it";
        if (existing is not null && !string.IsNullOrEmpty(_saved.Password)) PasswordBox.PlaceholderText = "Saved - type to replace it";
        ShowLocalHint();
        ShowFields();
        Opened += (_, _) => (existing is null ? (Control)(ProjectBox.Text is { Length: > 0 } ? HostBox : ProjectBox) : NameBox).Focus();
        Closed += (_, _) => _test?.Cancel();
    }

    /// <summary>The server as saved - null until Save, and when cancelled.</summary>
    internal DeployConnection? Result => _result;

    /// <summary>The server as saved, or null when cancelled.</summary>
    public static async Task<DeployConnection?> ShowAsync(DeployConnections connections, string projectsFolder, IReadOnlyList<string> projects,
        DeployConnection? existing)
    {
        var dialog = new ServerDialog(connections, projectsFolder, projects, existing);
        if (Dialogs.Owner is { } owner) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return dialog._result;
    }

    private string Site => (_existing?.Site ?? ProjectBox.Text ?? "").Trim();
    private string ProjectFolder => Site.Length > 0 ? Path.Combine(_projectsFolder, Site) : _projectsFolder;
    private TransferProtocol Protocol => ProtocolBox.SelectedIndex == 1 ? TransferProtocol.Ftp : TransferProtocol.Sftp;
    private FtpEncryption Encryption => (FtpEncryption)Math.Max(0, EncryptionBox.SelectedIndex);
    private DeployAuth Logon => LogonBox.SelectedItem is string text ? Logons.First(l => l.Text == text).Auth : DeployAuth.Password;
    private bool IsPlain => Protocol == TransferProtocol.Ftp && Encryption == FtpEncryption.PlainOnly;

    /// <summary>The logon types the protocol has - SFTP no anonymous, FTP no key file.</summary>
    private void FillLogons(DeployAuth select)
    {
        var logons = Logons.Where(l => Protocol == TransferProtocol.Sftp ? l.Auth != DeployAuth.Anonymous : l.Auth != DeployAuth.PrivateKey)
            .Select(l => l.Text).ToList();
        LogonBox.ItemsSource = logons;
        var wanted = Logons.First(l => l.Auth == select).Text;
        LogonBox.SelectedItem = logons.Contains(wanted) ? wanted : Logons.First(l => l.Auth == DeployAuth.Password).Text;
    }

    private void Protocol_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (LogonBox is null) return;
        FillLogons(Logon);
        // Another protocol is another identity: an SSH key isn't a certificate.
        _trusted = _existing is not null && _existing.Protocol == Protocol ? _existing.TrustedHostKey : null;
        ShowFields();
    }

    private void Encryption_Changed(object? sender, SelectionChangedEventArgs e) => ShowFields();

    private void Logon_Changed(object? sender, SelectionChangedEventArgs e) => ShowFields();

    private void ShowFields()
    {
        if (PasswordBox is null) return;
        var ftp = Protocol == TransferProtocol.Ftp;
        EncryptionLabel.IsVisible = EncryptionBox.IsVisible = ftp;
        PortBox.PlaceholderText = DeployConnection.DefaultPort(Protocol, Encryption).ToString();
        var logon = Logon;
        UserLabel.IsVisible = UserBox.IsVisible = logon != DeployAuth.Anonymous;
        PasswordLabel.IsVisible = PasswordBox.IsVisible = logon is DeployAuth.Password or DeployAuth.AskForPassword;
        PasswordLabel.Text = logon == DeployAuth.AskForPassword ? "Password (test):" : "Password:";
        KeyLabel.IsVisible = KeyRow.IsVisible = PassphraseLabel.IsVisible = PassphraseBox.IsVisible = logon == DeployAuth.PrivateKey;
        ShowIdentity();
    }

    private void ShowIdentity() => IdentityLine.Text = IsPlain
        ? "Plain FTP has no identity to check - the password and files travel unencrypted."
        : _trusted is null
            ? "The server's identity isn't trusted yet - Test connection shows it."
            : $"Trusted identity: {_trusted}";

    private void ShowLocalHint() => LocalHint.Text = (LocalBox.Text ?? "").Trim().Length == 0
        ? $"Empty: the project's folder - {ProjectFolder}"
        : "The folder on this computer this server is compared with, pushed from and pulled into.";

    private async void ChooseLocal_Click(object? sender, RoutedEventArgs e)
    {
        var start = (LocalBox.Text ?? "").Trim() is { Length: > 0 } typed && Directory.Exists(typed) ? typed
            : Directory.Exists(ProjectFolder) ? ProjectFolder : _projectsFolder;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "The project's folder on this computer",
            SuggestedStartLocation = Directory.Exists(start) ? await StorageProvider.TryGetFolderFromPathAsync(start) : null
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) LocalBox.Text = path;
    }

    private async void ChooseKey_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Private key (OpenSSH or PuTTY)", AllowMultiple = false });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024)
            {
                TestResult.Text = $"{info.Name} is too large to be a private key.";
                return;
            }
            _key = await File.ReadAllTextAsync(path);
            KeyBox.Text = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestResult.Text = $"Couldn't read {path}: {ex.Message}";
        }
    }

    private DeployConnection Read() =>
        (_existing ?? _connections.New(Site)) with
        {
            Site = Site,
            Name = (NameBox.Text ?? "").Trim(),
            Environment = EnvironmentBox.SelectedItem is DeployEnvironment env ? env : DeployEnvironment.Production,
            Protocol = Protocol,
            Encryption = Encryption,
            Host = (HostBox.Text ?? "").Trim(),
            // Empty: the usual port of the protocol and encryption.
            Port = (PortBox.Text ?? "").Trim() is { Length: > 0 } port ? int.TryParse(port, out var p) ? p : 0 : DeployConnection.DefaultPort(Protocol, Encryption),
            UserName = Logon == DeployAuth.Anonymous ? "" : (UserBox.Text ?? "").Trim(),
            Auth = Logon,
            RemoteRoot = (RootBox.Text ?? "").Trim(),
            // The project's own folder chosen: none of its own, so it follows the project.
            LocalRoot = (LocalBox.Text ?? "").Trim() is { Length: > 0 } local &&
                        !Path.TrimEndingDirectorySeparator(local).Equals(Path.TrimEndingDirectorySeparator(ProjectFolder), StringComparison.OrdinalIgnoreCase)
                ? local : null,
            IgnoreRules = RulesBox.Text ?? "",
            TrustedHostKey = IsPlain ? null : _trusted
        };

    // What is typed, or what is saved when a field was left empty.
    private DeploySecrets Secrets()
    {
        var password = PasswordBox.Text ?? "";
        var passphrase = PassphraseBox.Text ?? "";
        return Logon switch
        {
            DeployAuth.PrivateKey => new DeploySecrets(null, _key ?? _saved.PrivateKey, passphrase.Length > 0 ? passphrase : _saved.Passphrase),
            DeployAuth.Anonymous => DeploySecrets.None,
            DeployAuth.AskForPassword => new DeploySecrets(password.Length > 0 ? password : null),
            _ => new DeploySecrets(password.Length > 0 ? password : _saved.Password)
        };
    }

    // Each problem by field, said at once - false when there is one.
    private bool Check(DeployConnection connection, DeploySecrets secrets)
    {
        var problems = DeployConnections.Validate(connection, connection.Auth == DeployAuth.AskForPassword ? secrets with { Password = "-" } : secrets)
            .Select(p => $"{p.Key}: {p.Value}").ToList();
        if (connection.Site.Length == 0) problems.Insert(0, "Project: choose the project this server is for, or type a name.");
        else if (connection.Site.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || connection.Site is "." or "..")
            problems.Insert(0, $"Project: '{connection.Site}' can't be a folder's name.");
        else if (connection.LocalRoot is null && !Directory.Exists(ProjectFolder))
            problems.Add($"LocalRoot: there is no folder {ProjectFolder} - choose the project's folder with Browse….");
        Problems.Text = string.Join(Environment.NewLine, problems);
        Problems.IsVisible = problems.Count > 0;
        return problems.Count == 0;
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        var connection = Read();
        var secrets = Secrets();
        if (!Check(connection, secrets)) return;
        if (connection.Auth == DeployAuth.AskForPassword && secrets.Password is null)
        {
            if (await Dialogs.PasswordAsync($"Password for {connection.UserName} on {connection.Host}:", "Test") is not { } typed) return;
            secrets = new DeploySecrets(typed);
        }
        await TestAsync(connection, secrets);
    }

    /// <summary>Tests the connection - and asks to trust the server's identity when it shows one not trusted yet. True when it works.</summary>
    private async Task<bool> TestAsync(DeployConnection connection, DeploySecrets secrets)
    {
        _test?.Cancel();
        _test = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = _test.Token;
        TestButton.IsEnabled = OkButton.IsEnabled = false;
        Testing.IsVisible = true;
        TestResult.Text = $"Connecting to {connection.Host}:{connection.Port}…";
        TestResult.Foreground = null;
        try
        {
            var check = await Task.Run(() => _connections.TestAsync(connection, secrets, token), token);
            if (check.NeedsTrust && check.Identity is { } identity && await TrustAsync(connection, check, identity))
            {
                _trusted = identity;
                ShowIdentity();
                check = await Task.Run(() => _connections.TestAsync(connection with { TrustedHostKey = identity }, secrets, token), token);
            }
            TestResult.Text = check.Message;
            TestResult.Foreground = check.Ok ? Avalonia.Media.Brushes.MediumSeaGreen : Avalonia.Media.Brushes.IndianRed;
            if (_existing is not null) _connections.RecordCheck(_existing.Id, check);
            return check.Ok;
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = $"{connection.Host} didn't answer within a minute.";
            TestResult.Foreground = Avalonia.Media.Brushes.IndianRed;
            return false;
        }
        finally
        {
            TestButton.IsEnabled = OkButton.IsEnabled = true;
            Testing.IsVisible = false;
        }
    }

    /// <summary>Shows the server's identity and asks whether to trust it - with a warning when it isn't the one trusted before.</summary>
    private static Task<bool> TrustAsync(DeployConnection connection, ConnectionCheck check, string identity)
    {
        var what = connection.Protocol == TransferProtocol.Sftp ? "host key" : "certificate";
        var details = check.IdentityDetails is null ? "" : $"{Environment.NewLine}({check.IdentityDetails})";
        if (check.IdentityChanged)
            return Dialogs.ConfirmAsync(
                $"{connection.Host}'s {what} CHANGED.{Environment.NewLine}{Environment.NewLine}Trusted before: {connection.TrustedHostKey}" +
                $"{Environment.NewLine}Shown now:       {identity}{details}{Environment.NewLine}{Environment.NewLine}" +
                "This happens when a server is reinstalled or its certificate renewed - or when another machine poses as it to catch your " +
                "password and files. Check with the server's administrator before you trust the new one.",
                "Trust the new one", "Don't trust it", danger: true);
        return Dialogs.ConfirmAsync(
            $"Connecting to {connection.Host} for the first time. Its {what}'s fingerprint is:{Environment.NewLine}{Environment.NewLine}" +
            $"{identity}{details}{Environment.NewLine}{Environment.NewLine}" +
            "If your hosting shows a fingerprint, check that it is this one. Once trusted, DNN Manager refuses a server that shows another.",
            "Trust this server", "Cancel");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var connection = Read();
        var secrets = Secrets();
        if (!Check(connection, secrets)) return;
        if (!IsPlain && connection.TrustedHostKey is null)
        {
            if (connection.Auth == DeployAuth.AskForPassword && secrets.Password is null &&
                await Dialogs.PasswordAsync($"Password for {connection.UserName} on {connection.Host}:", "Test") is { } typed)
                secrets = new DeploySecrets(typed);
            if (!await TestAsync(connection, secrets)) return;
            // TLS if available, on a server without it, is plain FTP - there was nothing to trust.
            if (_trusted is null && connection.Encryption != FtpEncryption.ExplicitIfAvailable) return;
            connection = connection with { TrustedHostKey = _trusted };
        }
        _connections.Save(connection, secrets);
        _result = _connections.Find(connection.Id);
        Close();
    }
}
