using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using FluentFTP;
using FluentFTP.Exceptions;

namespace DnnManager.Infrastructure.Deployment;

/// <summary>
/// A server's folder over FTP (FluentFTP) - with TLS as <see cref="DeployConnection.Encryption"/> says, whose certificate
/// must be the one the user trusted (its SHA-256, <c>SHA256:…</c>), whatever Windows thinks of its issuer: a self-signed
/// certificate is common on hosting, and pinning it is stronger than a chain. Plain FTP has no identity to check - its
/// password crosses the network as it is. Always binary: a file arrives byte for byte. FTP can't replace a file in one
/// step (the old one is deleted first), can't make a file exclusively (the lock is a check, then a write) and can't
/// resume an upload from a stream it reads once - so an interrupted file is sent again from its start.
/// </summary>
public sealed class FtpFileSystem(DeployConnection connection, DeploySecrets secrets) : IRemoteFileSystem
{
    private AsyncFtpClient? _ftp;
    private string _root = "/";
    private bool? _canHash;
    private string? _rejected;
    private string? _details;
    private bool _disposed;

    public string? PresentedIdentity { get; private set; }
    public string? ServerDescription { get; private set; }
    public bool CanResumeUpload => false;

    private AsyncFtpClient Ftp => _ftp ?? throw new InvalidOperationException("Not connected.");

    // The accounts that signed in this session, with that password: a 530 for one of them later is the server refusing another sign-in for a
    // while (Azure does after many in a row) - not a wrong password.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> SignedIn = new(StringComparer.OrdinalIgnoreCase);

    // With a fingerprint of the password: a refusal after the password was changed to a wrong one is a wrong password.
    private string Account => $"{connection.UserName}@{connection.Host}:{connection.Port}#" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secrets.Password ?? "")))[..16];

    // How long a server that refused a sign-in is given before the next try.
    private static readonly TimeSpan SignInBackOff = TimeSpan.FromSeconds(20);

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ftp = NewClient();
        try
        {
            await Ftp.Connect(ct);
            SignedIn[Account] = true;
        }
        catch (Exception ex) when (_rejected is not null && ex is AuthenticationException or FtpInvalidCertificateException or IOException or FtpException)
        {
            throw new UntrustedServerException(_rejected, connection.TrustedHostKey, _details);
        }
        catch (Exception ex) when (connection.Encryption != FtpEncryption.RequireImplicit && ex is IOException && _rejected is null)
        {
            // Cut off while signing in: a server that only takes FTP over TLS does that to plain FTP (an Azure App Service
            // set to "FTPS only"); a connection on the implicit port does it to explicit TLS.
            throw new RemoteException($"{connection.Host} closed the connection while DNN Manager signed in ({ex.Message.TrimEnd('.')}). " +
                (connection.Encryption is FtpEncryption.PlainOnly or FtpEncryption.ExplicitIfAvailable
                    ? "The server most likely accepts FTP only over TLS (an Azure App Service set to \"FTPS only\" does this) - choose Require explicit or Require implicit FTP over TLS."
                    : $"Check the port: {connection.Port} with explicit TLS - implicit TLS uses 990."), true, ex);
        }
        catch (TimeoutException ex) when (connection.Encryption != FtpEncryption.RequireImplicit && connection.Port == 990)
        {
            throw new RemoteException($"{connection.Host} didn't answer on port 990 - that is implicit TLS's port: choose Require implicit FTP over TLS, or port 21.", false, ex);
        }
        catch (Exception ex) when (Map(ex, $"connect to {connection.Host}") is { } mapped)
        {
            throw mapped;
        }
        await AfterConnectAsync(ct);
    }

    /// <summary>
    /// A client for the connection - not connected yet - that checks the server's certificate against the one trusted.
    /// </summary>
    private AsyncFtpClient NewClient()
    {
        var config = new FtpConfig
        {
            EncryptionMode = connection.Encryption switch
            {
                FtpEncryption.ExplicitIfAvailable => FtpEncryptionMode.Auto,
                FtpEncryption.RequireExplicit => FtpEncryptionMode.Explicit,
                FtpEncryption.RequireImplicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.None
            },
            DataConnectionEncryption = connection.Encryption != FtpEncryption.PlainOnly,
            ValidateAnyCertificate = false,
            ConnectTimeout = 30_000,
            ReadTimeout = 120_000,
            DataConnectionConnectTimeout = 30_000,
            DataConnectionReadTimeout = 120_000,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            DownloadDataType = FtpDataType.Binary,
            UploadDataType = FtpDataType.Binary,
            ListingDataType = FtpDataType.Binary,
            // DNN Manager tries again itself, on a new connection (RemotePool).
            RetryAttempts = 1
        };
        var (user, password) = connection.Auth == DeployAuth.Anonymous ? ("anonymous", "anonymous@") : (connection.UserName, secrets.Password ?? "");
        var client = new AsyncFtpClient(connection.Host, user, password, connection.Port, config);
        client.ValidateCertificate += (_, e) =>
        {
            var presented = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.Certificate.GetRawCertData())).TrimEnd('=');
            PresentedIdentity = presented;
            _details = $"{e.Certificate.Subject}, issued by {e.Certificate.Issuer}" +
                       (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None ? " - Windows trusts it" : $" - Windows doesn't trust it ({e.PolicyErrors})");
            e.Accept = connection.TrustedHostKey is { } trusted && string.Equals(trusted, presented, StringComparison.Ordinal);
            if (!e.Accept) _rejected = presented;
        };
        return client;
    }

    private async Task AfterConnectAsync(CancellationToken ct)
    {
        ServerDescription = Ftp.ServerType.ToString() is { } type && type != "Unknown" ? type : null;
        var root = DeployPath.NormalizeRemoteRoot(connection.RemoteRoot) ?? throw new RemoteException($"'{connection.RemoteRoot}' isn't a folder DNN Manager uses.", false);
        if (!root.StartsWith('/'))
        {
            var home = await Guard(() => Ftp.GetWorkingDirectory(ct), "read the home folder");
            root = home.TrimEnd('/') + "/" + root;
        }
        _root = root;
    }

    public Task<bool> RootExistsAsync(CancellationToken ct) => Guard(() => Ftp.DirectoryExists(_root, ct), $"look at {_root}");

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(Func<string, bool> skipFolder, Action<string> warn, Action<string> unreadable, CancellationToken ct)
    {
        // FTP lists one folder per command, each over a data connection of its own: a DNN site's thousand folders one after
        // the other take minutes. A level of the tree at a time, its folders shared out over up to Parallel connections.
        // A server that cuts connections off or limits sign-ins (Azure) brings it down to fewer: an extra connection the
        // server cut off gives its folder back and stops - only this one signs in again, after the server's wait.
        var files = new System.Collections.Concurrent.ConcurrentBag<RemoteFile>();
        var extra = new List<AsyncFtpClient>();
        for (var i = 1; i < Math.Clamp(connection.Parallel, 1, 8); i++)
        {
            var client = NewClient();
            try
            {
                await client.Connect(ct);
                extra.Add(client);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Refused (too many at once, a sign-in limit): done without.
                await client.DisposeAsync();
                break;
            }
        }
        var alive = new HashSet<AsyncFtpClient>(extra);
        List<string> level = [_root];
        try
        {
            while (level.Count > 0)
            {
                var queue = new System.Collections.Concurrent.ConcurrentQueue<string>(level);
                var next = new System.Collections.Concurrent.ConcurrentBag<string>();
                // Until every folder of the level is listed - one given back after a worker stopped is taken by another.
                while (!queue.IsEmpty)
                {
                    List<AsyncFtpClient> workers;
                    lock (alive) workers = [Ftp, .. alive];
                    await Task.WhenAll(workers.Select(client => Task.Run(async () =>
                    {
                        var primary = ReferenceEquals(client, _ftp);
                        while (queue.TryDequeue(out var folder))
                        {
                            ct.ThrowIfCancellationRequested();
                            FtpListItem[] items;
                            try
                            {
                                items = primary
                                    ? await ListFolderAsync(client, folder, ct)
                                    : await Guard(() => client.GetListing(folder, FtpListOption.AllFiles, ct), $"list {folder}", client);
                            }
                            catch (RemoteException ex) when (!primary && ex.Transient)
                            {
                                queue.Enqueue(folder);
                                lock (alive) alive.Remove(client);
                                return;
                            }
                            foreach (var item in items) Take(folder, item, next);
                        }
                    }, ct)));
                }
                level = [.. next.OrderBy(f => f, StringComparer.Ordinal)];
            }
        }
        finally
        {
            foreach (var client in extra)
            {
                try { if (client.IsConnected) await client.Disconnect(CancellationToken.None); }
                catch (Exception) { /* closing a connection the server may have dropped */ }
                await client.DisposeAsync();
            }
        }
        return [.. files];

        void Take(string folder, FtpListItem item, System.Collections.Concurrent.ConcurrentBag<string> next)
        {
            if (item.Name is "." or "..") return;
            var full = folder.TrimEnd('/') + "/" + item.Name;
            if (!DeployPath.IsValidName(item.Name) || DeployPath.RelativeTo(_root, full) is not { } relative)
            {
                warn($"Left out {full} on the server: its name can't be used on Windows.");
                return;
            }
            switch (item.Type)
            {
                case FtpObjectType.Directory:
                    if (!skipFolder(relative)) next.Add(full);
                    break;
                case FtpObjectType.File:
                    files.Add(new RemoteFile(relative, item.Size, Time(item.Modified), Mode(item.Chmod)));
                    break;
                default:
                    warn($"Left out {relative} on the server: it is a link.");
                    break;
            }
        }
    }

    // How often one folder's listing is tried, on a connection made anew each time the server cut the last one off.
    private const int FolderAttempts = 4;

    /// <summary>
    /// One folder's listing - again on a new connection when the server cuts it off. Azure's FTP closes an explicit-TLS
    /// connection now and then after a listing has come in, while its "226 Transfer complete" is awaited: tried again
    /// here, folder by folder, a listing of thousands of folders isn't started over at each - and never gets through.
    /// </summary>
    private async Task<FtpListItem[]> ListFolderAsync(AsyncFtpClient client, string folder, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Guard(() => client.GetListing(folder, FtpListOption.AllFiles, ct), $"list {folder}", client);
            }
            catch (RemoteException ex) when (ex.Transient && attempt < FolderAttempts && !ct.IsCancellationRequested)
            {
                await ReconnectAsync(client, ct);
            }
        }
    }

    /// <summary>
    /// A new connection in place of one the server closed - signed in and its identity checked as the first was; waiting as
    /// long as the server asks between refused sign-ins - a few times, then the refusal stands.
    /// </summary>
    private async Task ReconnectAsync(AsyncFtpClient client, CancellationToken ct)
    {
        try { if (client.IsConnected) await client.Disconnect(ct); }
        catch (Exception ex) when (ex is IOException or FtpException or ObjectDisposedException or NullReferenceException or InvalidOperationException) { }
        var wait = TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            await Task.Delay(wait, ct);
            try
            {
                await Guard(async () =>
                {
                    await client.Connect(ct);
                    return true;
                }, $"connect to {connection.Host} again", client);
                return;
            }
            catch (RemoteException ex) when (ex.RetryAfter is { } after && attempt < 4)
            {
                wait = after;
            }
        }
    }

    public async Task<RemoteFile?> StatAsync(string path, CancellationToken ct)
    {
        // Looked up in its folder's listing (LIST -a): a file's own LIST hides a name starting with a dot (the part a file
        // is sent under) on vsftpd, and its time must be to the same precision as the scan's listing - a listing of
        // minutes and an MDTM of seconds would differ for a file nobody touched.
        var full = Full(path);
        var folder = DeployPath.Remote(_root, DeployPath.Folder(path)).TrimEnd('/');
        if (DeployPath.Folder(path).Length == 0) folder = _root;
        var name = DeployPath.Name(path);
        var items = await Guard(async () =>
        {
            if (!await Ftp.DirectoryExists(folder, ct)) return [];
            return await Ftp.GetListing(folder, FtpListOption.AllFiles, ct);
        }, $"look at {path}");
        var item = items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        if (item is null) return null;
        if (item.Type == FtpObjectType.Directory) throw new RemoteException($"{path} is a folder on the server.", false);
        // A listing that left the size out: asked for it.
        var size = item.Size >= 0 ? item.Size : await Guard(() => Ftp.GetFileSize(full, -1, ct), $"read the size of {path}");
        return size < 0 ? null : new RemoteFile(path, size, Time(item.Modified), Mode(item.Chmod));
    }

    public Task DownloadAsync(string path, Stream destination, long offset, Action<long>? progress, CancellationToken ct) =>
        Guard(async () =>
        {
            var report = progress is null ? null : new SyncProgress<FtpProgress>(p => progress(p.TransferredBytes));
            if (!await Ftp.DownloadStream(destination, Full(path), offset, report, ct))
                throw new RemoteException($"Could not read {path} from the server.", true);
            return true;
        }, $"read {path}");

    public Task UploadAsync(Stream source, string path, long offset, Action<long>? progress, CancellationToken ct) =>
        Guard(async () =>
        {
            if (offset != 0) throw new RemoteException("FTP sends a file again from its start.", false);
            var report = progress is null ? null : new SyncProgress<FtpProgress>(p => progress(p.TransferredBytes));
            var status = await Ftp.UploadStream(source, Full(path), FtpRemoteExists.Overwrite, true, report, ct);
            if (status == FtpStatus.Failed) throw new RemoteException($"Could not send {path} to the server.", true);
            return true;
        }, $"send {path}");

    public Task ReplaceAsync(string from, string to, CancellationToken ct) =>
        Guard(async () => await Ftp.MoveFile(Full(from), Full(to), FtpRemoteExists.Overwrite, ct), $"put {to} in place");

    public Task DeleteAsync(string path, CancellationToken ct) =>
        Guard(async () =>
        {
            var full = Full(path);
            if (await Ftp.FileExists(full, ct)) await Ftp.DeleteFile(full, ct);
            return true;
        }, $"delete {path}");

    public async Task DeleteEmptyFoldersAsync(string folder, CancellationToken ct)
    {
        for (var relative = folder; relative.Length > 0; relative = DeployPath.Folder(relative))
        {
            var full = Full(relative);
            var empty = await Guard(async () => await Ftp.DirectoryExists(full, ct) &&
                                                (await Ftp.GetListing(full, FtpListOption.AllFiles, ct)).All(i => i.Name is "." or ".."),
                $"look at the folder {relative}");
            if (!empty) return;
            // RMD alone: a server removes only an empty folder - FluentFTP's DeleteDirectory would take whatever is in it,
            // a file another transfer just put there too.
            var removed = await Guard(async () => (await Ftp.Execute($"RMD {full}", ct)).Success, $"delete the folder {relative}");
            if (!removed) return;
        }
    }

    public async Task SetPermissionsAsync(string path, int mode, CancellationToken ct)
    {
        try
        {
            // SITE CHMOD takes the octal digits: 0644 as 644.
            await Ftp.SetFilePermissions(Full(path), int.Parse(Convert.ToString(mode & 0x1FF, 8)), ct);
        }
        catch (FtpCommandException)
        {
            // A server without SITE CHMOD (Windows' IIS FTP): the file keeps its default.
        }
        catch (Exception ex) when (Map(ex, $"set the permissions of {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>?> TryHashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (_canHash == false) return null;
        var hashes = new Dictionary<string, string>(DeployPath.Comparer);
        foreach (var path in paths)
        {
            try
            {
                var hash = await Ftp.GetChecksum(Full(path), FtpHashAlgorithm.SHA256, ct);
                if (!hash.IsValid || hash.Algorithm != FtpHashAlgorithm.SHA256) throw new FtpHashUnsupportedException();
                hashes[path] = hash.Value.ToLowerInvariant();
                _canHash = true;
            }
            catch (Exception ex) when (ex is FtpHashUnsupportedException or NotImplementedException or FtpCommandException && _canHash != true)
            {
                // No HASH command (vsftpd, IIS): the files are read back to hash them.
                _canHash = false;
                return null;
            }
            catch (Exception ex) when (Map(ex, "ask the server for hashes") is { } mapped)
            {
                throw mapped;
            }
        }
        return hashes;
    }

    public Task<bool> TryCreateAsync(string path, byte[] content, CancellationToken ct) =>
        Guard(async () =>
        {
            // FTP has no exclusive create: looked for, then written - two starting in the same instant could both pass.
            var full = Full(path);
            if (await Ftp.FileExists(full, ct)) return false;
            using var stream = new MemoryStream(content);
            await Ftp.UploadStream(stream, full, FtpRemoteExists.NoCheck, true, null, ct);
            return true;
        }, $"make {path}");

    private async Task<T> Guard<T>(Func<Task<T>> work, string what, AsyncFtpClient? client = null)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (Map(ex, what, client) is { } mapped)
        {
            throw mapped;
        }
    }

    private string Full(string relative) =>
        DeployPath.Normalize(relative) is { } safe ? DeployPath.Remote(_root, safe) : throw new RemoteException($"'{relative}' isn't a path DNN Manager uses.", false);

    private static DateTime? Time(DateTime modified) => modified == DateTime.MinValue ? null : DateTime.SpecifyKind(modified, DateTimeKind.Utc);

    /// <summary>FTP's chmod (644 as its digits) as a Unix mode - null when the server didn't say.</summary>
    private static int? Mode(int chmod)
    {
        if (chmod <= 0) return null;
        try { return Convert.ToInt32(chmod.ToString(), 8); }
        catch (FormatException) { return null; }
    }

    private RemoteException? Map(Exception ex, string what, AsyncFtpClient? client = null) => ex switch
    {
        RemoteException or OperationCanceledException or InvalidDataException => null,
        FtpAuthenticationException when SignedIn.ContainsKey(Account) => new RemoteException(
            $"{connection.Host} refused another sign-in for now - it limits how many come in a short time. Trying again in a moment.", true, ex)
            { RetryAfter = SignInBackOff },
        FtpAuthenticationException => new RemoteException(
            $"Signing in to {connection.Host} as {connection.UserName} was refused - check the user name and password. If they are right, the server " +
            "may be refusing sign-ins for a few minutes after many in a row: wait, then try again.", false, ex),
        FtpCommandException { CompletionCode: { Length: > 0 } code } when code[0] == '4' => new RemoteException($"Could not {what}: {ex.Message}", true, ex),
        FtpCommandException => new RemoteException($"Could not {what}: {ex.Message}", false, ex),
        FtpSecurityNotAvailableException => new RemoteException($"{connection.Host} doesn't offer TLS - it can only be reached with plain FTP.", false, ex),
        AuthenticationException => new RemoteException($"Could not {what}: TLS failed - {ex.Message}", false, ex),
        TimeoutException or IOException or SocketException or FtpException => new RemoteException($"Could not {what}: {ex.Message}", true, ex),
        ObjectDisposedException or InvalidOperationException when !_disposed => new RemoteException($"Could not {what}: the connection was lost.", true, ex),
        // FluentFTP throws this, not an IOException, when the server closed the connection while it waited for a reply.
        NullReferenceException when !_disposed && (client ?? _ftp) is { IsConnected: false } =>
            new RemoteException($"Could not {what}: the server closed the connection.", true, ex),
        _ => null
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ftp is null) return;
        try
        {
            if (_ftp.IsConnected) await _ftp.Disconnect(CancellationToken.None);
        }
        catch (Exception) { /* closing a broken connection */ }
        await _ftp.DisposeAsync();
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>Makes the SFTP or FTP(S) file system of a connection.</summary>
public sealed class RemoteFileSystemFactory : IRemoteFileSystemFactory
{
    public IRemoteFileSystem Create(DeployConnection connection, DeploySecrets secrets) => connection.Protocol switch
    {
        TransferProtocol.Sftp => new SftpFileSystem(connection, secrets),
        _ => new FtpFileSystem(connection, secrets)
    };
}
