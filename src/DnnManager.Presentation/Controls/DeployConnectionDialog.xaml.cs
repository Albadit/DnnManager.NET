using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Presentation.Services;
using Microsoft.Win32;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Adds or edits one of a project's servers, as FileZilla's Site Manager does: protocol, host and port, FTP's encryption,
/// the logon type, user and password. The password, key and passphrase are saved encrypted (DPAPI) and never shown again
/// - a field left empty keeps what is saved; "Ask for password" saves none. A server's identity (SSH host key, TLS
/// certificate) is trusted only once the user has seen it (Test connection); one that changes is refused until trusted
/// again, with a warning.
/// </summary>
public partial class DeployConnectionDialog : Window
{
    private readonly DeployConnections _connections;
    private readonly string _site;
    private readonly DeployConnection? _existing;
    private readonly DeploySecrets _saved;
    private string? _trusted;
    private string? _key;
    private CancellationTokenSource? _test;

    // The choices as FileZilla words them, in the order of their enums.
    // FTP first, as FileZilla lists it.
    private static readonly (TransferProtocol Protocol, string Text)[] Protocols =
        [(TransferProtocol.Ftp, "FTP - File Transfer Protocol"), (TransferProtocol.Sftp, "SFTP - SSH File Transfer Protocol")];
    private static readonly string[] Encryptions =
        ["Use explicit FTP over TLS if available", "Require explicit FTP over TLS", "Require implicit FTP over TLS", "Only use plain FTP (insecure)"];
    private static readonly (DeployAuth Auth, string Text)[] Logons =
        [(DeployAuth.Anonymous, "Anonymous"), (DeployAuth.Password, "Normal"), (DeployAuth.AskForPassword, "Ask for password"), (DeployAuth.PrivateKey, "Key file")];

    internal DeployConnectionDialog(DeployConnections connections, string site, DeployConnection? existing)
    {
        _connections = connections;
        _site = site;
        _existing = existing;
        _saved = existing is null ? DeploySecrets.None : connections.Secrets(existing.Id);
        _trusted = existing?.TrustedHostKey;
        InitializeComponent();
        ThemeManager.Track(this);
        Title = existing is null ? $"Add a server to '{site}'" : $"Edit '{existing.Name}'";
        EnvironmentBox.ItemsSource = Enum.GetValues<DeployEnvironment>();
        ProtocolBox.ItemsSource = Protocols.Select(p => p.Text).ToList();
        EncryptionBox.ItemsSource = Encryptions;

        var c = existing ?? connections.New(site);
        NameBox.Text = c.Name;
        EnvironmentBox.SelectedItem = c.Environment;
        ProtocolBox.SelectedIndex = Array.FindIndex(Protocols, p => p.Protocol == c.Protocol);
        EncryptionBox.SelectedIndex = (int)c.Encryption;
        HostBox.Text = c.Host;
        // The usual port is shown as nothing, as FileZilla shows it.
        PortBox.Text = existing is null || c.Port == DeployConnection.DefaultPort(c.Protocol, c.Encryption) ? "" : c.Port.ToString();
        FillLogons(c.Auth);
        UserBox.Text = c.UserName;
        RootBox.Text = c.RemoteRoot;
        ParallelBox.Text = c.Parallel.ToString();
        BackupsBox.IsChecked = c.KeepBackups;
        VerifyBox.IsChecked = c.Verify == VerifyMode.Checksum;
        RulesBox.Text = c.IgnoreRules;
        if (!string.IsNullOrEmpty(_saved.PrivateKey)) KeyStatus.Text = "A key is saved - Browse… to replace it.";
        ShowFields();
        Loaded += (_, _) => (existing is null ? HostBox : NameBox).Focus();
        Closed += (_, _) => _test?.Cancel();
    }

    /// <summary>The connection as saved, or null when cancelled.</summary>
    public static DeployConnection? Show(DeployConnections connections, string site, DeployConnection? existing)
    {
        var dialog = new DeployConnectionDialog(connections, site, existing)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private DeployConnection? Result { get; set; }

    private TransferProtocol Protocol => Protocols[Math.Max(0, ProtocolBox.SelectedIndex)].Protocol;
    private FtpEncryption Encryption => (FtpEncryption)Math.Max(0, EncryptionBox.SelectedIndex);
    private DeployAuth Logon => LogonBox.SelectedItem is string text ? Logons.First(l => l.Text == text).Auth : DeployAuth.Password;
    private bool IsPlain => Protocol == TransferProtocol.Ftp && Encryption == FtpEncryption.PlainOnly;

    /// <summary>The logon types the protocol has - SFTP no anonymous, FTP no key file - with <paramref name="select"/> chosen if it is one.</summary>
    private void FillLogons(DeployAuth select)
    {
        var logons = Logons.Where(l => Protocol == TransferProtocol.Sftp ? l.Auth != DeployAuth.Anonymous : l.Auth != DeployAuth.PrivateKey)
            .Select(l => l.Text).ToList();
        LogonBox.ItemsSource = logons;
        var wanted = Logons.First(l => l.Auth == select).Text;
        LogonBox.SelectedItem = logons.Contains(wanted) ? wanted : Logons.First(l => l.Auth == DeployAuth.Password).Text;
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || LogonBox is null) return;
        FillLogons(Logon);
        // Another protocol is another identity: an SSH key isn't a certificate.
        _trusted = _existing is not null && _existing.Protocol == Protocol ? _existing.TrustedHostKey : null;
        ShowFields();
    }

    private void Encryption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ShowFields();
    }

    private void Logon_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ShowFields();
    }

    /// <summary>What the protocol, encryption and logon type need - and nothing else.</summary>
    private void ShowFields()
    {
        if (LogonBox is null || PasswordRow is null) return;
        var ftp = Protocol == TransferProtocol.Ftp;
        EncryptionRow.Visibility = ftp ? Visibility.Visible : Visibility.Collapsed;
        PlainFtpWarning.Visibility = ftp && Encryption is FtpEncryption.PlainOnly or FtpEncryption.ExplicitIfAvailable ? Visibility.Visible : Visibility.Collapsed;
        PlainFtpText.Text = Encryption == FtpEncryption.PlainOnly
            ? "Plain FTP sends your password and your files unencrypted - anyone on the network between you and the server can read them, and there is no way to check you reached the real server. Some servers refuse it (an Azure App Service set to \"FTPS only\")."
            : "If the server doesn't offer TLS, this falls back to plain FTP - your password and files then cross the network unencrypted. Require explicit or implicit TLS when the server has it.";
        var logon = Logon;
        UserRow.Visibility = logon == DeployAuth.Anonymous ? Visibility.Collapsed : Visibility.Visible;
        PasswordRow.Visibility = logon is DeployAuth.Password or DeployAuth.AskForPassword ? Visibility.Visible : Visibility.Collapsed;
        KeyRow.Visibility = PassphraseRow.Visibility = logon == DeployAuth.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Text = logon == DeployAuth.AskForPassword
            ? "Not saved: DNN Manager asks for it the first time it reaches the server, and forgets it when it closes. What you type here is used for the test."
            : "A password is saved - leave this empty to keep it.";
        PasswordHint.Visibility = logon == DeployAuth.AskForPassword || (logon == DeployAuth.Password && !string.IsNullOrEmpty(_saved.Password))
            ? Visibility.Visible : Visibility.Collapsed;
        ShowIdentity();
    }

    // The label opens and closes the section too, as the chevron does.
    private void MoreLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => MoreToggle.IsChecked = MoreToggle.IsChecked != true;

    private void ChooseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the private key",
            Filter = "Private keys|*.pem;*.key;*.ppk;id_*|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 64 * 1024) throw new IOException("It is too big to be a private key.");
            var text = File.ReadAllText(dialog.FileName);
            if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal) && !text.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
                throw new IOException("It isn't a private key (OpenSSH, PEM or PuTTY).");
            _key = text;
            KeyStatus.Text = $"{Path.GetFileName(dialog.FileName)} - saved encrypted when you save.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error($"{dialog.FileName} can't be used: {ex.Message}");
        }
    }

    private DeployConnection Read() =>
        (_existing ?? new DeployConnection { Site = _site, Name = "", Host = "", UserName = "", RemoteRoot = "" }) with
        {
            Name = NameBox.Text.Trim(),
            Environment = EnvironmentBox.SelectedItem is DeployEnvironment env ? env : DeployEnvironment.Production,
            Protocol = Protocol,
            Encryption = Encryption,
            Host = HostBox.Text.Trim(),
            // Empty: the usual port of the protocol and encryption.
            Port = PortBox.Text.Trim().Length == 0 ? DeployConnection.DefaultPort(Protocol, Encryption)
                : int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
            UserName = Logon == DeployAuth.Anonymous ? "" : UserBox.Text.Trim(),
            Auth = Logon,
            RemoteRoot = RootBox.Text.Trim(),
            Parallel = int.TryParse(ParallelBox.Text.Trim(), out var parallel) ? parallel : 0,
            KeepBackups = BackupsBox.IsChecked == true,
            Verify = VerifyBox.IsChecked == true ? VerifyMode.Checksum : VerifyMode.Size,
            IgnoreRules = RulesBox.Text,
            TrustedHostKey = IsPlain ? null : _trusted
        };

    // What is typed, or what is saved when a field was left empty.
    private DeploySecrets Secrets() => Logon switch
    {
        DeployAuth.PrivateKey => new DeploySecrets(null, _key ?? _saved.PrivateKey, PassphraseBox.Password.Length > 0 ? PassphraseBox.Password : _saved.Passphrase),
        DeployAuth.Anonymous => DeploySecrets.None,
        DeployAuth.AskForPassword => new DeploySecrets(PasswordBox.Password.Length > 0 ? PasswordBox.Password : null),
        _ => new DeploySecrets(PasswordBox.Password.Length > 0 ? PasswordBox.Password : _saved.Password)
    };

    private bool Check(DeployConnection connection, DeploySecrets secrets)
    {
        var problems = DeployConnections.Validate(connection, secrets).ToDictionary();
        // Ask for password: none is saved - typed here, it is only for the test.
        if (connection.Auth == DeployAuth.AskForPassword) problems.Remove("Password");
        Problem.Text = string.Join(Environment.NewLine, problems.Values);
        Problem.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        return problems.Count == 0;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var connection = Read();
        var secrets = Secrets();
        if (!Check(connection, secrets)) return;
        if (connection.Auth == DeployAuth.AskForPassword && secrets.Password is null)
        {
            if (InputDialog.ShowSecret($"Password for {connection.UserName} on {connection.Host}:", "Test") is not { } typed) return;
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
        Testing.IsIndeterminate = true;
        Testing.Visibility = Visibility.Visible;
        TestResult.Text = $"Connecting to {connection.Host}:{connection.Port}…";
        TestResult.SetResourceReference(ForegroundProperty, "TextMuted");
        try
        {
            var check = await Task.Run(() => _connections.TestAsync(connection, secrets, token), token);
            if (check.NeedsTrust && check.Identity is { } identity && Trust(connection, check, identity))
            {
                _trusted = identity;
                ShowIdentity();
                check = await Task.Run(() => _connections.TestAsync(connection with { TrustedHostKey = identity }, secrets, token), token);
            }
            TestResult.Text = check.Message;
            TestResult.SetResourceReference(ForegroundProperty, check.Ok ? "SuccessText" : "ErrorText");
            if (_existing is not null) _connections.RecordCheck(_existing.Id, check);
            return check.Ok;
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = $"{connection.Host} didn't answer within a minute.";
            TestResult.SetResourceReference(ForegroundProperty, "ErrorText");
            return false;
        }
        finally
        {
            if (IsLoaded)
            {
                TestButton.IsEnabled = OkButton.IsEnabled = true;
                Testing.IsIndeterminate = false;
                Testing.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Shows the server's identity and asks whether to trust it - with a warning when it isn't the one trusted before.</summary>
    private bool Trust(DeployConnection connection, ConnectionCheck check, string identity)
    {
        var what = connection.Protocol == TransferProtocol.Sftp ? "host key" : "certificate";
        var details = check.IdentityDetails is null ? "" : $"{Environment.NewLine}({check.IdentityDetails})";
        if (check.IdentityChanged)
            return Dialogs.ConfirmDanger(
                $"{connection.Host}'s {what} CHANGED.{Environment.NewLine}{Environment.NewLine}Trusted before: {connection.TrustedHostKey}" +
                $"{Environment.NewLine}Shown now:       {identity}{details}{Environment.NewLine}{Environment.NewLine}" +
                "This happens when a server is reinstalled or its certificate renewed - or when another machine poses as it to catch your " +
                "password and files. Check with the server's administrator before you trust the new one.",
                "Trust the new one", "Don't trust it");
        return Dialogs.Confirm(
            $"Connecting to {connection.Host} for the first time. Its {what}'s fingerprint is:{Environment.NewLine}{Environment.NewLine}" +
            $"{identity}{details}{Environment.NewLine}{Environment.NewLine}" +
            "If your hosting shows a fingerprint, check that it is this one. Once trusted, DNN Manager refuses a server that shows another.",
            "Trust this server", "Cancel");
    }

    private void ShowIdentity() => IdentityLine.Text = IsPlain
        ? "Plain FTP has no identity to check."
        : _trusted is null
            ? "The server's identity isn't trusted yet - Test connection shows it."
            : $"Trusted identity: {_trusted}";

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        var connection = Read();
        var secrets = Secrets();
        if (!Check(connection, secrets)) return;
        if (!IsPlain && connection.TrustedHostKey is null)
        {
            if (connection.Auth == DeployAuth.AskForPassword && secrets.Password is null &&
                InputDialog.ShowSecret($"Password for {connection.UserName} on {connection.Host}:", "Test") is { } typed)
                secrets = new DeploySecrets(typed);
            if (!await TestAsync(connection, secrets)) return;
            // TLS if available, on a server without it, is plain FTP - there was nothing to trust.
            if (_trusted is null && connection.Encryption != FtpEncryption.ExplicitIfAvailable) return;
            connection = connection with { TrustedHostKey = _trusted };
        }
        _connections.Save(connection, secrets);
        Result = _connections.Find(connection.Id);
        DialogResult = true;
    }
}
