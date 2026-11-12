using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>What a connection test found.</summary>
/// <param name="Identity">The server's identity it showed (<c>SHA256:…</c>) - to trust when <paramref name="NeedsTrust"/>.</param>
/// <param name="IdentityChanged">It isn't the identity trusted before - which may be another machine posing as it.</param>
public sealed record ConnectionCheck(bool Ok, string Message, string? Identity = null, bool NeedsTrust = false, bool IdentityChanged = false,
    string? IdentityDetails = null, string? Server = null);

/// <summary>
/// A project's connections: checked before they are saved, tested (signing in, the server's identity, its folder),
/// saved with their secrets encrypted apart from them, and removed with everything kept for them.
/// </summary>
public sealed class DeployConnections(IDeployStore store, IRemoteFileSystemFactory factory,
    Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
{
    /// <summary>A new server of <paramref name="site"/>, with what Settings → Deploy gives a new one.</summary>
    public DeployConnection New(string site)
    {
        var defaults = options?.Value.Deploy ?? new Configuration.DeployOptions();
        return new DeployConnection
        {
            Site = site, Name = "Live", Host = "", UserName = "", RemoteRoot = "/", IgnoreRules = defaults.IgnoreRules,
            Parallel = defaults.Parallel, KeepBackups = defaults.KeepBackups, Verify = defaults.VerifyUploads ? VerifyMode.Checksum : VerifyMode.Size
        };
    }

    /// <summary>Whether the Deploy page compares by itself when it is opened (Settings → Deploy).</summary>
    public bool CompareOnOpen => options?.Value.Deploy.CompareOnOpen ?? true;

    public IReadOnlyList<DeployConnection> For(string site) => store.Connections(site);

    /// <summary>The projects that have a server.</summary>
    public IReadOnlyList<string> Sites() => store.Sites();

    public DeployConnection? Find(Guid id) => store.Connection(id);

    /// <summary>Whether a password, key or passphrase is saved for the connection - never the secret itself.</summary>
    public (bool Password, bool Key, bool Passphrase) SavedSecrets(Guid id)
    {
        var secrets = store.ReadSecrets(id);
        return (!string.IsNullOrEmpty(secrets.Password), !string.IsNullOrEmpty(secrets.PrivateKey), !string.IsNullOrEmpty(secrets.Passphrase));
    }

    /// <summary>The secrets saved for the connection - for a test of it as edited, when the user left them as they were.</summary>
    internal DeploySecrets Secrets(Guid id) => store.ReadSecrets(id);

    /// <summary>What is wrong with the connection as filled in, by field - empty when nothing is.</summary>
    public static IReadOnlyDictionary<string, string> Validate(DeployConnection connection, DeploySecrets secrets)
    {
        var problems = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(connection.Name)) problems["Name"] = "Give it a name, e.g. Live.";
        if (string.IsNullOrWhiteSpace(connection.Host)) problems["Host"] = "The server's name or address.";
        else if (Uri.CheckHostName(connection.Host.Trim()) == UriHostNameType.Unknown) problems["Host"] = $"'{connection.Host}' isn't a server name or address.";
        if (connection.Port is < 1 or > 65535) problems["Port"] = "A port from 1 to 65535.";
        if (connection.Auth != DeployAuth.Anonymous && string.IsNullOrWhiteSpace(connection.UserName)) problems["UserName"] = "The user to sign in as.";
        if (connection.Auth == DeployAuth.Anonymous && connection.Protocol == TransferProtocol.Sftp) problems["Auth"] = "SFTP has no anonymous sign-in.";
        if (DeployPath.NormalizeRemoteRoot(connection.RemoteRoot) is null)
            problems["RemoteRoot"] = "The project's folder on the server, e.g. /var/www/shop - without '..'.";
        if (!string.IsNullOrWhiteSpace(connection.LocalRoot) && (!Path.IsPathFullyQualified(connection.LocalRoot) || !Directory.Exists(connection.LocalRoot)))
            problems["LocalRoot"] = $"'{connection.LocalRoot}' isn't a folder on this PC - choose one with Browse…, or leave it empty for the project's folder.";
        if (connection.Parallel is < 1 or > 16) problems["Parallel"] = "From 1 to 16 files at a time.";
        if (connection.Auth == DeployAuth.Password && string.IsNullOrEmpty(secrets.Password)) problems["Password"] = "The password.";
        if (connection.Auth == DeployAuth.PrivateKey)
        {
            if (connection.Protocol != TransferProtocol.Sftp) problems["Auth"] = "A private key signs in over SFTP only.";
            else if (string.IsNullOrWhiteSpace(secrets.PrivateKey)) problems["PrivateKey"] = "Choose the private key file (OpenSSH or PuTTY format).";
        }
        if (IgnoreRules.Parse(connection.IgnoreRules).Problems is { Count: > 0 } rules) problems["IgnoreRules"] = string.Join(Environment.NewLine, rules);
        return problems;
    }

    /// <summary>
    /// Connects and signs in as <paramref name="connection"/> says, checks the server's identity and that its folder is
    /// there. Changes nothing - on the server or here.
    /// </summary>
    public async Task<ConnectionCheck> TestAsync(DeployConnection connection, DeploySecrets secrets, CancellationToken ct)
    {
        try
        {
            await using var fs = factory.Create(connection, secrets);
            await fs.ConnectAsync(ct);
            var server = fs.ServerDescription;
            if (!await fs.RootExistsAsync(ct))
                return new ConnectionCheck(false, $"Signed in, but the folder {connection.RemoteRoot} isn't there.", fs.PresentedIdentity, Server: server);
            return new ConnectionCheck(true, $"Connected to {connection.Host}{(server is null ? "" : $" ({server})")} - {connection.RemoteRoot} is there.",
                fs.PresentedIdentity, Server: server);
        }
        catch (UntrustedServerException ex)
        {
            return new ConnectionCheck(false, ex.Message, ex.Presented, NeedsTrust: true, IdentityChanged: !ex.FirstTime, IdentityDetails: ex.Details);
        }
        catch (RemoteException ex)
        {
            return new ConnectionCheck(false, ex.Message);
        }
    }

    /// <summary>Saves the connection - and its secrets, when <paramref name="secrets"/> is given (null keeps the ones saved).</summary>
    public void Save(DeployConnection connection, DeploySecrets? secrets)
    {
        var normalized = connection with
        {
            Name = connection.Name.Trim(), Host = connection.Host.Trim(), UserName = connection.UserName.Trim(),
            RemoteRoot = DeployPath.NormalizeRemoteRoot(connection.RemoteRoot) ?? connection.RemoteRoot
        };
        store.Save(normalized);
        if (secrets is not null && connection.Auth == DeployAuth.AskForPassword)
        {
            if (!string.IsNullOrEmpty(secrets.Password)) store.RememberForSession(connection.Id, secrets.Password);
            store.SaveSecrets(connection.Id, DeploySecrets.None);
        }
        else if (secrets is not null)
        {
            store.SaveSecrets(connection.Id, connection.Auth == DeployAuth.Anonymous ? DeploySecrets.None : secrets);
        }
    }

    /// <summary>"Ask for password" and nothing typed yet this session: the password must be asked before the server is reached.</summary>
    public bool NeedsPassword(DeployConnection connection) => connection.Auth == DeployAuth.AskForPassword && !store.HasSessionPassword(connection.Id);

    public void RememberForSession(Guid id, string password) => store.RememberForSession(id, password);

    public void RecordCheck(Guid id, ConnectionCheck check) => store.RecordCheck(id, DateTime.UtcNow, check.Ok ? null : check.Message);

    /// <summary>Removes the connection, its secrets, baseline and history. The project's and the server's files stay.</summary>
    public void Delete(Guid id) => store.Delete(id);
}
