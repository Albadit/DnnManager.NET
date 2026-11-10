namespace DnnManager.Domain;

/// <summary>How DNN Manager reaches a server it deploys to - FTP's encryption is a choice of its own (<see cref="FtpEncryption"/>).</summary>
public enum TransferProtocol
{
    /// <summary>SFTP over SSH - encrypted, with the server's host key checked.</summary>
    Sftp,
    /// <summary>FTP - encrypted with TLS or not, as <see cref="DeployConnection.Encryption"/> says.</summary>
    Ftp
}

/// <summary>How FTP is encrypted - FileZilla's four choices.</summary>
public enum FtpEncryption
{
    /// <summary>TLS when the server offers it (AUTH TLS), plain FTP when it doesn't.</summary>
    ExplicitIfAvailable,
    /// <summary>TLS asked for on the usual port (AUTH TLS, port 21) - refused when the server can't.</summary>
    RequireExplicit,
    /// <summary>TLS from the first byte, on its own port (990) - what an Azure App Service's "FTPS" is too.</summary>
    RequireImplicit,
    /// <summary>No encryption: the password and the files cross the network as they are.</summary>
    PlainOnly
}

/// <summary>Which of a project's servers a connection is - Production asks more before it changes anything.</summary>
public enum DeployEnvironment { Development, Staging, Production }

/// <summary>How DNN Manager signs in to the server - FileZilla's logon types.</summary>
public enum DeployAuth
{
    /// <summary>"Normal": a user name and a saved password.</summary>
    Password,
    /// <summary>"Key file" (SFTP): a private key, with its passphrase.</summary>
    PrivateKey,
    /// <summary>FTP's anonymous sign-in.</summary>
    Anonymous,
    /// <summary>A user name, and the password asked once each time DNN Manager runs - never saved.</summary>
    AskForPassword
}

/// <summary>How an upload is checked once it is on the server.</summary>
public enum VerifyMode
{
    /// <summary>The file's SHA-256 on the server (asked of it, or read back) must be the one sent.</summary>
    Checksum,
    /// <summary>Only its size - quicker on a slow line, but a damaged file of the right size goes unnoticed.</summary>
    Size
}

/// <summary>
/// A server a project deploys to, and where on it: its folder (<see cref="RemoteRoot"/>) is the project's folder there.
/// Its secrets (password, private key, passphrase) are kept apart from it, encrypted - never in this record.
/// </summary>
public sealed record DeployConnection
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The project (IIS site) the connection belongs to.</summary>
    public required string Site { get; init; }

    /// <summary>What the user calls it, e.g. "Live" - shown with its environment.</summary>
    public required string Name { get; init; }
    public DeployEnvironment Environment { get; init; } = DeployEnvironment.Production;
    public TransferProtocol Protocol { get; init; } = TransferProtocol.Sftp;

    /// <summary>FTP's encryption - for SFTP it doesn't apply.</summary>
    public FtpEncryption Encryption { get; init; } = FtpEncryption.RequireExplicit;
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string UserName { get; init; }
    public DeployAuth Auth { get; init; } = DeployAuth.Password;

    /// <summary>The project's folder on the server, e.g. <c>/var/www/shop</c> or <c>/httpdocs</c>.</summary>
    public required string RemoteRoot { get; init; }

    /// <summary>
    /// The server's identity as the user trusted it: for SFTP its host key's SHA-256 fingerprint (<c>SHA256:…</c>), for
    /// FTPS its certificate's SHA-256 - null until trusted. A server that shows another one is refused.
    /// </summary>
    public string? TrustedHostKey { get; init; }

    /// <summary>The ignore rules, like a .gitignore - one pattern a line.</summary>
    public string IgnoreRules { get; init; } = "";

    /// <summary>How many files are sent or fetched at the same time - each over a connection of its own.</summary>
    public int Parallel { get; init; } = 4;

    /// <summary>Whether what a push or pull overwrites or deletes is kept first, to roll back to.</summary>
    public bool KeepBackups { get; init; } = true;
    public VerifyMode Verify { get; init; } = VerifyMode.Checksum;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>When it was last reached, and what went wrong then (null: it worked).</summary>
    public DateTime? LastCheckedUtc { get; init; }
    public string? LastError { get; init; }

    /// <summary>When a push or pull last finished on it.</summary>
    public DateTime? LastSyncUtc { get; init; }

    public bool IsProduction => Environment == DeployEnvironment.Production;

    /// <summary>Plain FTP: nothing is encrypted, and the server has no identity to check.</summary>
    public bool IsPlainFtp => Protocol == TransferProtocol.Ftp && Encryption == FtpEncryption.PlainOnly;

    /// <summary>"Live (Production) - sftp://deploy@example.com:22/var/www/shop".</summary>
    public string Describe() => $"{Name} ({Environment}) - {Address}";

    public string Address => $"{Scheme}://{(Auth == DeployAuth.Anonymous ? "" : UserName + "@")}{Host}:{Port}{(RemoteRoot.StartsWith('/') ? "" : "/")}{RemoteRoot}";

    private string Scheme => Protocol == TransferProtocol.Sftp ? "sftp" : Encryption switch
    {
        FtpEncryption.RequireImplicit => "ftps",
        FtpEncryption.RequireExplicit => "ftpes",
        _ => "ftp"
    };

    /// <summary>The usual port: 22 for SFTP, 990 for implicit FTP over TLS, 21 for the rest of FTP.</summary>
    public static int DefaultPort(TransferProtocol protocol, FtpEncryption encryption = FtpEncryption.RequireExplicit) =>
        protocol == TransferProtocol.Sftp ? 22 : encryption == FtpEncryption.RequireImplicit ? 990 : 21;
}

/// <summary>
/// What is known of a file at one place: its size, its SHA-256 (lowercase hex; null when it wasn't needed - see
/// <see cref="SyncItem"/>) and its time as that place reports it.
/// </summary>
public sealed record FileState(long Size, string? Hash, DateTime? ModifiedUtc);

/// <summary>
/// A file as it was when the project and the server last agreed on it - the common ancestor a change is measured from,
/// as in git. <see cref="RemoteSize"/> and <see cref="RemoteModifiedUtc"/> are how the server listed it then: the same
/// listing now means it still holds <see cref="Hash"/>, without reading it again. Null when the server's copy isn't
/// known to be <see cref="Hash"/>.
/// </summary>
public sealed record BaselineEntry(string Path, string Hash, long Size, long? RemoteSize, DateTime? RemoteModifiedUtc);

/// <summary>Where a file stands between the project (local) and the server (remote), measured from the baseline.</summary>
public enum SyncStatus
{
    Synchronized,
    /// <summary>New in the project.</summary>
    LocalAdded,
    LocalModified,
    /// <summary>Deleted from the project, still on the server.</summary>
    LocalDeleted,
    /// <summary>New on the server.</summary>
    RemoteAdded,
    RemoteModified,
    /// <summary>Deleted from the server, still in the project.</summary>
    RemoteDeleted,
    /// <summary>Changed on both sides, differently.</summary>
    Conflict
}

/// <summary>How a <see cref="SyncStatus.Conflict"/> came about.</summary>
public enum ConflictKind
{
    None,
    /// <summary>Changed in the project and on the server.</summary>
    BothModified,
    /// <summary>Made in both, with different contents, without a baseline.</summary>
    BothAdded,
    /// <summary>Changed in the project, deleted from the server.</summary>
    LocalModifiedRemoteDeleted,
    /// <summary>Deleted from the project, changed on the server.</summary>
    LocalDeletedRemoteModified
}

/// <summary>Which way a change goes.</summary>
public enum SyncDirection { None, Push, Pull, Conflict }

/// <summary>
/// One file of a comparison: where it stands, and what each side and the baseline hold. <see cref="Sensitive"/>: it
/// looks like a secret or an environment's configuration (web.config, a key, .env) - never staged by "all".
/// </summary>
public sealed record SyncItem(string Path, SyncStatus Status, ConflictKind Conflict, FileState? Local, FileState? Remote,
    BaselineEntry? Base, bool Sensitive = false)
{
    public SyncDirection Direction => Status switch
    {
        SyncStatus.LocalAdded or SyncStatus.LocalModified or SyncStatus.LocalDeleted => SyncDirection.Push,
        SyncStatus.RemoteAdded or SyncStatus.RemoteModified or SyncStatus.RemoteDeleted => SyncDirection.Pull,
        SyncStatus.Conflict => SyncDirection.Conflict,
        _ => SyncDirection.None
    };

    /// <summary>A push or pull of it deletes a file.</summary>
    public bool IsDeletion => Status is SyncStatus.LocalDeleted or SyncStatus.RemoteDeleted;

    /// <summary>What the status means, in words - "Modified on the server", "Changed in both".</summary>
    public string Describe() => Status switch
    {
        SyncStatus.Synchronized => "The same in both",
        SyncStatus.LocalAdded => "Added in the project",
        SyncStatus.LocalModified => "Modified in the project",
        SyncStatus.LocalDeleted => "Deleted from the project",
        SyncStatus.RemoteAdded => "Added on the server",
        SyncStatus.RemoteModified => "Modified on the server",
        SyncStatus.RemoteDeleted => "Deleted from the server",
        _ => Conflict switch
        {
            ConflictKind.BothAdded => "Added in both, differently",
            ConflictKind.LocalModifiedRemoteDeleted => "Modified in the project, deleted from the server",
            ConflictKind.LocalDeletedRemoteModified => "Deleted from the project, modified on the server",
            _ => "Modified in both"
        }
    };
}

/// <summary>What an operation of the deployment history was.</summary>
public enum DeployOperationKind { Push, Pull, Rollback, Resolve }

public enum DeployOperationStatus
{
    Running,
    Completed,
    /// <summary>It failed and what it had changed was put back.</summary>
    Failed,
    Cancelled,
    /// <summary>It failed and not everything could be put back - the files say which.</summary>
    Partial
}

/// <summary>Which side of a connection a file was changed on.</summary>
public enum SyncSide { Local, Remote }

/// <summary>What happened to one file in an operation.</summary>
public enum FileAction { Added, Modified, Deleted }

/// <summary>
/// One push, pull, rollback or conflict resolution - a deployment "commit": its message, who ran it, and each file it
/// changed with its contents before and after (<see cref="DeployOperationFile"/>), kept to look at and to roll back.
/// </summary>
public sealed record DeployOperation
{
    public long Id { get; init; }
    public Guid ConnectionId { get; init; }
    public required string Site { get; init; }
    public required string ConnectionName { get; init; }
    public DeployEnvironment Environment { get; init; }
    public DeployOperationKind Kind { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }

    /// <summary>The Windows account that ran it (DOMAIN\user).</summary>
    public required string User { get; init; }
    public string Message { get; init; } = "";
    public DeployOperationStatus Status { get; init; }
    public string? Error { get; init; }

    /// <summary>How many transfers were tried again after a network error.</summary>
    public int Retries { get; init; }

    /// <summary>For a rollback: the operation it took back.</summary>
    public long? RollbackOf { get; init; }

    /// <summary>Taken back by this rollback.</summary>
    public long? RolledBackBy { get; init; }

    /// <summary>A review pull: the files went to a folder of their own, not into the project.</summary>
    public string? ReviewFolder { get; init; }
    public int FileCount { get; init; }
}

/// <summary>
/// One file of an operation: what was done to it, where, and its SHA-256 before and after (null: there was no file).
/// <see cref="BaseBefore"/> is its baseline before the operation - what a rollback puts back too.
/// </summary>
public sealed record DeployOperationFile
{
    public long OperationId { get; init; }
    public required string Path { get; init; }
    public SyncSide Side { get; init; }
    public FileAction Action { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public string? BaseBefore { get; init; }

    /// <summary>The size of <see cref="BaseBefore"/>'s content.</summary>
    public long? BaseBeforeSize { get; init; }

    /// <summary>The conflict the file had, and how it was resolved ("Kept the project's", "Took the server's", "Merged").</summary>
    public string? Conflict { get; init; }
    public string? Resolution { get; init; }

    /// <summary>Why the file wasn't done - null when it was.</summary>
    public string? Error { get; init; }
    public int Attempts { get; init; } = 1;
    public bool Done => Error is null;
}
