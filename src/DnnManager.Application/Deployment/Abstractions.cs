using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>A file on the server, by its path relative to the connection's folder, as the server lists it.</summary>
/// <param name="Permissions">Its Unix mode (<c>0644</c> as the number 420), when the server says.</param>
public sealed record RemoteFile(string Path, long Size, DateTime? ModifiedUtc, int? Permissions = null);

/// <summary>A connection's secrets - only what its way of signing in needs. Never logged, shown or written unencrypted.</summary>
public sealed record DeploySecrets(string? Password = null, string? PrivateKey = null, string? Passphrase = null)
{
    public static DeploySecrets None { get; } = new();
}

/// <summary>
/// The server's folder, through SFTP or FTP(S) - one connection, for one transfer at a time (a transfer that runs in
/// parallel gets its own). Every path is relative to the connection's folder and checked (<see cref="DeployPath"/>):
/// nothing outside it is listed, read, written or deleted. A failure is a <see cref="RemoteException"/>.
/// </summary>
public interface IRemoteFileSystem : IAsyncDisposable
{
    /// <summary>
    /// Connects and signs in, checking the server is the one trusted (<see cref="DeployConnection.TrustedHostKey"/>):
    /// one not trusted yet or showing another identity throws <see cref="UntrustedServerException"/>.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Every file under the folder (links and special files left out), relative. <paramref name="skipFolder"/> is asked
    /// of each folder (relative) before it is gone into; <paramref name="warn"/> hears of what was left out and why;
    /// <paramref name="unreadable"/> of each folder that couldn't be listed (relative, ending in <c>/</c>) - what is in it
    /// isn't known, so it must not count as deleted.
    /// </summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(Func<string, bool> skipFolder, Action<string> warn, Action<string> unreadable, CancellationToken ct);

    /// <summary>Whether the connection's folder is there.</summary>
    Task<bool> RootExistsAsync(CancellationToken ct);

    /// <summary>The file at <paramref name="path"/> - null when there is none.</summary>
    Task<RemoteFile?> StatAsync(string path, CancellationToken ct);

    /// <summary>Writes the file's bytes from <paramref name="offset"/> on into <paramref name="destination"/>.</summary>
    Task DownloadAsync(string path, Stream destination, long offset, Action<long>? progress, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="source"/> (read from its position) to <paramref name="path"/> - from <paramref name="offset"/>
    /// on, keeping the bytes before it (resuming a file sent in part) - making its folders as needed.
    /// </summary>
    Task UploadAsync(Stream source, string path, long offset, Action<long>? progress, CancellationToken ct);

    /// <summary>Puts <paramref name="from"/> in <paramref name="to"/>'s place - atomically where the server can (SFTP's posix-rename).</summary>
    Task ReplaceAsync(string from, string to, CancellationToken ct);

    /// <summary>Deletes the file; nothing happens when there is none.</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    /// <summary>Deletes <paramref name="folder"/> and the folders above it (up to the connection's) that are empty.</summary>
    Task DeleteEmptyFoldersAsync(string folder, CancellationToken ct);

    /// <summary>Sets the file's Unix mode - quietly nothing where the server doesn't allow it.</summary>
    Task SetPermissionsAsync(string path, int mode, CancellationToken ct);

    /// <summary>
    /// The SHA-256 of each of <paramref name="paths"/> as the server computes it (sha256sum over SSH, FTP's HASH) - without
    /// sending the file - or null when the server can't. A path missing from what comes back has none.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> TryHashAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>
    /// Makes <paramref name="path"/> with <paramref name="content"/> only when there is none - atomically where the server
    /// can (SFTP's exclusive create). False when it is there already.
    /// </summary>
    Task<bool> TryCreateAsync(string path, byte[] content, CancellationToken ct);

    /// <summary>The server's identity it showed when connecting (<c>SHA256:…</c>), for the user to trust - null for plain FTP.</summary>
    string? PresentedIdentity { get; }

    /// <summary>Whether <see cref="UploadAsync"/> can go on from an offset - a file sent in part is resumed, not sent again.</summary>
    bool CanResumeUpload { get; }

    /// <summary>What the server is, for the connection test - "OpenSSH_9.6", "vsFTPd 3.0.5".</summary>
    string? ServerDescription { get; }
}

/// <summary>Makes the <see cref="IRemoteFileSystem"/> of a connection - SFTP or FTP(S) by its protocol.</summary>
public interface IRemoteFileSystemFactory
{
    IRemoteFileSystem Create(DeployConnection connection, DeploySecrets secrets);
}

/// <summary>A failure talking to the server. <see cref="Transient"/>: the network or the server's load - worth trying again.</summary>
public class RemoteException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;

    /// <summary>
    /// How long to wait before trying again, when the server says how long it needs - a server refusing sign-ins for a
    /// while after many in a row; null: the usual back-off.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// The server isn't the one trusted: it shows <see cref="Presented"/>, and either nothing was trusted yet
/// (<see cref="FirstTime"/>) or something else was - which may be another machine posing as it.
/// </summary>
public sealed class UntrustedServerException(string presented, string? trusted, string? details = null) : RemoteException(
    trusted is null
        ? $"The server's identity isn't trusted yet: {presented}."
        : $"The server's identity changed: it shows {presented}, but {trusted} was trusted. Another machine may be posing as it - " +
          "or the server was reinstalled. Check with its administrator before trusting it again.",
    transient: false)
{
    public string Presented { get; } = presented;
    public string? Trusted { get; } = trusted;
    public bool FirstTime => Trusted is null;

    /// <summary>More about the identity for the user to check - the key's type, the certificate's subject and issuer.</summary>
    public string? Details { get; } = details;
}

/// <summary>
/// DNN Manager's records of deployments, in its database: the connections, each one's baseline, the hashes of the
/// project's files (to hash only what changed), and the history of every push, pull and rollback.
/// </summary>
public interface IDeployStore
{
    IReadOnlyList<DeployConnection> Connections(string site);

    /// <summary>The projects (sites) that have a server - on macOS and Linux, where there is no IIS to list them.</summary>
    IReadOnlyList<string> Sites();
    DeployConnection? Connection(Guid id);

    /// <summary>Adds or replaces the connection (not its secrets - <see cref="SaveSecrets"/>).</summary>
    void Save(DeployConnection connection);

    /// <summary>Removes the connection with its secrets, its baseline and its history.</summary>
    void Delete(Guid id);

    void RecordCheck(Guid id, DateTime atUtc, string? error);
    void RecordSync(Guid id, DateTime atUtc);

    /// <summary>The connection's secrets - for "Ask for password" the password typed this session, none before.</summary>
    DeploySecrets ReadSecrets(Guid id);

    /// <summary>"Ask for password": keeps <paramref name="password"/> in memory until DNN Manager ends - never saved.</summary>
    void RememberForSession(Guid id, string password);

    bool HasSessionPassword(Guid id);
    void SaveSecrets(Guid id, DeploySecrets secrets);

    IReadOnlyDictionary<string, BaselineEntry> Baseline(Guid id);

    /// <summary>Sets the entries of <paramref name="set"/> and removes those of <paramref name="remove"/> - in one transaction.</summary>
    void UpdateBaseline(Guid id, IEnumerable<BaselineEntry> set, IEnumerable<string> remove);

    /// <summary>The hashes kept of the files under <paramref name="root"/>, by relative path, with the size and time they were for.</summary>
    IReadOnlyDictionary<string, CachedHash> LocalHashes(string root);

    /// <summary>Keeps the hashes of <paramref name="root"/>'s files - all of them: what isn't in <paramref name="files"/> is forgotten.</summary>
    void SaveLocalHashes(string root, IReadOnlyDictionary<string, CachedHash> files);

    /// <summary>Records an operation that starts now; returns its number.</summary>
    long Begin(DeployOperation operation);
    void AddFiles(long operationId, IEnumerable<DeployOperationFile> files);
    void End(long operationId, DeployOperationStatus status, string? error, int retries);

    /// <summary>The operation fetched into this folder instead of the project.</summary>
    void SetReviewFolder(long operationId, string folder);
    void MarkRolledBack(long operationId, long rollbackId);
    IReadOnlyList<DeployOperation> Operations(Guid connectionId, int limit = 200);
    DeployOperation? Operation(long id);
    IReadOnlyList<DeployOperationFile> Files(long operationId);

    /// <summary>The contents the history and the baselines still refer to - what the snapshot store keeps.</summary>
    IReadOnlySet<string> ReferencedHashes(DateTime historySinceUtc);
}

/// <summary>A file's hash as it was for one size and time.</summary>
public sealed record CachedHash(long Size, long ModifiedTicks, string Hash);

/// <summary>
/// File contents kept by their SHA-256 (as git keeps objects): what a push or pull overwrote or deleted, what it wrote,
/// and what a scan fetched to compare. A rollback puts them back; a diff shows them. Read back only through
/// <see cref="OpenVerified"/>, which checks the content is still what its name says.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Keeps what <paramref name="content"/> holds from its position on; returns its hash.</summary>
    Task<string> AddAsync(Stream content, CancellationToken ct);

    /// <summary>Keeps <paramref name="file"/>; returns its hash.</summary>
    Task<string> AddFileAsync(string file, CancellationToken ct);

    /// <summary>A content to be written into the store - a download, say - kept once it is complete and wanted.</summary>
    ISnapshotWriter BeginAdd();

    bool Contains(string hash);

    /// <summary>The size of the content kept as <paramref name="hash"/> - null when it isn't kept.</summary>
    long? SizeOf(string hash);

    /// <summary>
    /// The content kept as <paramref name="hash"/>, to read once from the start - throws <see cref="InvalidDataException"/>
    /// at the end of it when it isn't that content any more (changed on disk). Null when it isn't kept.
    /// </summary>
    Stream? OpenVerified(string hash);

    /// <summary>The bytes kept as <paramref name="hash"/>, checked - null when it isn't kept or isn't that content.</summary>
    Task<byte[]?> ReadAsync(string hash, long maxBytes, CancellationToken ct);

    /// <summary>Forgets what isn't in <paramref name="keep"/> and is older than <paramref name="minAge"/>; returns how many.</summary>
    int Prune(IReadOnlySet<string> keep, TimeSpan minAge);
}

/// <summary>A content being written into the <see cref="ISnapshotStore"/>: nothing is kept until <see cref="CompleteAsync"/>.</summary>
public interface ISnapshotWriter : IAsyncDisposable
{
    /// <summary>Where the content is written.</summary>
    Stream Stream { get; }

    /// <summary>
    /// Ends the writing and returns the content's hash - kept when <paramref name="keep"/> says so of that hash (a content
    /// the project has anyway isn't worth keeping twice), forgotten otherwise.
    /// </summary>
    Task<string> CompleteAsync(Func<string, bool> keep, CancellationToken ct);
}
