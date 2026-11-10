using System.Net.Sockets;
using System.Text;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DnnManager.Infrastructure.Deployment;

/// <summary>
/// A server's folder over SFTP (SSH.NET). The server's host key must be the one the user trusted - its SHA-256
/// fingerprint as <c>ssh</c> shows it (<c>SHA256:…</c>) - or the connection is refused before signing in. Files are
/// replaced with OpenSSH's posix-rename (atomic), new files made exclusively (the lock). Hashes are asked of the server
/// with <c>sha256sum</c> over SSH when its account may run commands; an SFTP-only account (a chroot) can't, and the
/// files are read back instead.
/// </summary>
public sealed class SftpFileSystem(DeployConnection connection, DeploySecrets secrets) : IRemoteFileSystem
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(2);

    private SftpClient? _sftp;
    private SshClient? _ssh;
    private string _root = "/";
    private string? _rejectedKey;
    private string? _keyDetails;
    private readonly HashSet<string> _madeFolders = new(StringComparer.Ordinal);
    private bool _disposed;

    public string? PresentedIdentity { get; private set; }
    public string? ServerDescription { get; private set; }
    public bool CanResumeUpload => true;

    private SftpClient Sftp => _sftp ?? throw new InvalidOperationException("Not connected.");

    public async Task ConnectAsync(CancellationToken ct)
    {
        _sftp = new SftpClient(Info()) { OperationTimeout = OperationTimeout };
        _sftp.HostKeyReceived += (_, e) => Check(e);
        try
        {
            await _sftp.ConnectAsync(ct);
        }
        catch (Exception ex) when (_rejectedKey is not null && ex is SshException or SocketException or IOException)
        {
            throw new UntrustedServerException(_rejectedKey, connection.TrustedHostKey, _keyDetails);
        }
        catch (Exception ex) when (Map(ex, "connect to") is { } mapped)
        {
            throw mapped;
        }
        ServerDescription = _sftp.ConnectionInfo.ServerVersion;
        var root = DeployPath.NormalizeRemoteRoot(connection.RemoteRoot) ?? throw new RemoteException($"'{connection.RemoteRoot}' isn't a folder DNN Manager uses.", false);
        _root = root.StartsWith('/') ? root : (_sftp.WorkingDirectory.TrimEnd('/') + "/" + root);
    }

    private ConnectionInfo Info()
    {
        AuthenticationMethod method;
        if (connection.Auth == DeployAuth.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(secrets.PrivateKey)) throw new RemoteException("No private key is saved for this connection.", false);
            PrivateKeyFile key;
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(secrets.PrivateKey));
                key = string.IsNullOrEmpty(secrets.Passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, secrets.Passphrase);
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or FormatException)
            {
                throw new RemoteException($"The private key can't be read: {ex.Message} - is its passphrase right?", false, ex);
            }
            method = new PrivateKeyAuthenticationMethod(connection.UserName, key);
        }
        else
        {
            if (connection.Auth == DeployAuth.AskForPassword && secrets.Password is null)
                throw new RemoteException("The password hasn't been given this session.", false);
            // The password, and the same answered to a server that asks for it interactively (keyboard-interactive).
            var password = secrets.Password ?? "";
            var interactive = new KeyboardInteractiveAuthenticationMethod(connection.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            return new ConnectionInfo(connection.Host, connection.Port, connection.UserName,
                new PasswordAuthenticationMethod(connection.UserName, password), interactive) { Timeout = ConnectTimeout, RetryAttempts = 1 };
        }
        return new ConnectionInfo(connection.Host, connection.Port, connection.UserName, method) { Timeout = ConnectTimeout, RetryAttempts = 1 };
    }

    private void Check(HostKeyEventArgs e)
    {
        var presented = "SHA256:" + e.FingerPrintSHA256;
        PresentedIdentity = presented;
        _keyDetails = $"{e.HostKeyName}, {e.KeyLength} bits";
        e.CanTrust = connection.TrustedHostKey is { } trusted && string.Equals(trusted, presented, StringComparison.Ordinal);
        if (!e.CanTrust) _rejectedKey = presented;
    }

    public async Task<bool> RootExistsAsync(CancellationToken ct)
    {
        try
        {
            return (await Sftp.GetAttributesAsync(_root, ct)).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (Map(ex, $"look at {_root}") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(Func<string, bool> skipFolder, Action<string> warn, Action<string> unreadable, CancellationToken ct)
    {
        var files = new List<RemoteFile>();
        var folders = new Queue<string>();
        folders.Enqueue(_root);
        while (folders.Count > 0)
        {
            var folder = folders.Dequeue();
            try
            {
                await foreach (var entry in Sftp.ListDirectoryAsync(folder, ct))
                {
                    if (entry.Name is "." or "..") continue;
                    var full = folder.TrimEnd('/') + "/" + entry.Name;
                    if (!DeployPath.IsValidName(entry.Name) || DeployPath.RelativeTo(_root, full) is not { } relative)
                    {
                        warn($"Left out {full} on the server: its name can't be used on Windows.");
                        continue;
                    }
                    if (entry.IsSymbolicLink)
                    {
                        warn($"Left out {relative} on the server: it is a symbolic link.");
                        continue;
                    }
                    if (entry.IsDirectory)
                    {
                        if (!skipFolder(relative)) folders.Enqueue(full);
                    }
                    else if (entry.IsRegularFile)
                    {
                        files.Add(new RemoteFile(relative, entry.Length, Utc(entry.LastWriteTimeUtc), Mode(entry.Attributes)));
                    }
                }
            }
            catch (SftpPermissionDeniedException)
            {
                var relative = DeployPath.RelativeTo(_root, folder) ?? "";
                warn($"Left out {(relative.Length == 0 ? folder : relative)}/ on the server: no permission to list it.");
                unreadable(relative + "/");
            }
            catch (Exception ex) when (Map(ex, $"list {folder}") is { } mapped)
            {
                throw mapped;
            }
        }
        return files;
    }

    public async Task<RemoteFile?> StatAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        try
        {
            var attributes = await Sftp.GetAttributesAsync(full, ct);
            if (attributes.IsDirectory) throw new RemoteException($"{path} is a folder on the server.", false);
            return new RemoteFile(path, attributes.Size, Utc(attributes.LastWriteTimeUtc), Mode(attributes));
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (Map(ex, $"look at {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task DownloadAsync(string path, Stream destination, long offset, Action<long>? progress, CancellationToken ct)
    {
        var full = Full(path);
        try
        {
            if (offset == 0)
            {
                var report = progress is null ? null : new SyncProgress<DownloadFileProgressReport>(r => progress((long)r.TotalBytesDownloaded));
                await Sftp.DownloadFileAsync(full, destination, report, ct);
                return;
            }
            await using var source = await Sftp.OpenAsync(full, FileMode.Open, FileAccess.Read, ct);
            source.Seek(offset, SeekOrigin.Begin);
            await CopyAsync(source, destination, progress, ct);
        }
        catch (Exception ex) when (Map(ex, $"read {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task UploadAsync(Stream source, string path, long offset, Action<long>? progress, CancellationToken ct)
    {
        var full = Full(path);
        try
        {
            await MakeFoldersAsync(DeployPath.Folder(path), ct);
            if (offset == 0)
            {
                var report = progress is null ? null : new SyncProgress<UploadFileProgressReport>(r => progress((long)r.TotalBytesUploaded));
                await Sftp.UploadFileAsync(source, full, true, report, ct);
                return;
            }
            await using var target = await Sftp.OpenAsync(full, FileMode.Open, FileAccess.Write, ct);
            target.Seek(offset, SeekOrigin.Begin);
            await CopyAsync(source, target, sent => progress?.Invoke(offset + sent), ct);
        }
        catch (Exception ex) when (Map(ex, $"send {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    private static async Task CopyAsync(Stream source, Stream target, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct);
            total += n;
            progress?.Invoke(total);
        }
    }

    private async Task MakeFoldersAsync(string relativeFolder, CancellationToken ct)
    {
        if (relativeFolder.Length == 0) return;
        var path = _root.TrimEnd('/');
        foreach (var part in relativeFolder.Split('/'))
        {
            path += "/" + part;
            if (_madeFolders.Contains(path)) continue;
            // As its folder lists it (lstat): a folder that is a symbolic link would take the file outside the connection's
            // folder - the scan leaves those out, and nothing is written through one.
            var parent = path[..path.LastIndexOf('/')];
            ISftpFile? entry = null;
            await foreach (var item in Sftp.ListDirectoryAsync(parent.Length == 0 ? "/" : parent, ct))
                if (item.Name == part)
                {
                    entry = item;
                    break;
                }
            if (entry is null) await Sftp.CreateDirectoryAsync(path, ct);
            else if (entry.IsSymbolicLink)
                throw new RemoteException($"{DeployPath.RelativeTo(_root, path)} is a symbolic link on the server - DNN Manager doesn't write through those.", false);
            else if (!entry.IsDirectory)
                throw new RemoteException($"{DeployPath.RelativeTo(_root, path)} is a file on the server, not a folder.", false);
            _madeFolders.Add(path);
        }
    }

    public async Task ReplaceAsync(string from, string to, CancellationToken ct)
    {
        string source = Full(from), target = Full(to);
        try
        {
            // OpenSSH's posix-rename replaces the file in one step - nobody ever sees it missing or half there.
            await Task.Run(() => Sftp.RenameFile(source, target, isPosix: true), ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or SshException && ex is not SftpPermissionDeniedException && ex is not SshConnectionException)
        {
            // A server without the extension: the old file goes first, then the new one takes its name.
            try
            {
                if (await Sftp.ExistsAsync(target, ct)) await Sftp.DeleteFileAsync(target, ct);
                await Sftp.RenameFileAsync(source, target, ct);
            }
            catch (Exception inner) when (Map(inner, $"put {to} in place") is { } mapped)
            {
                throw mapped;
            }
        }
        catch (Exception ex) when (Map(ex, $"put {to} in place") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        try
        {
            await Sftp.DeleteFileAsync(Full(path), ct);
        }
        catch (SftpPathNotFoundException)
        {
        }
        catch (Exception ex) when (Map(ex, $"delete {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task DeleteEmptyFoldersAsync(string folder, CancellationToken ct)
    {
        for (var relative = folder; relative.Length > 0; relative = DeployPath.Folder(relative))
        {
            var full = Full(relative);
            try
            {
                var any = false;
                await foreach (var entry in Sftp.ListDirectoryAsync(full, ct))
                {
                    if (entry.Name is "." or "..") continue;
                    any = true;
                    break;
                }
                if (any) return;
                await Sftp.DeleteDirectoryAsync(full, ct);
                _madeFolders.Remove(full);
            }
            catch (SftpPathNotFoundException)
            {
            }
            catch (Exception ex) when (Map(ex, $"delete the folder {relative}") is { } mapped)
            {
                throw mapped;
            }
        }
    }

    public async Task SetPermissionsAsync(string path, int mode, CancellationToken ct)
    {
        // SSH.NET takes the mode as its octal digits written as a number: 0644 as 644.
        var digits = short.Parse(Convert.ToString(mode & 0x1FF, 8));
        try
        {
            await Task.Run(() => Sftp.ChangePermissions(Full(path), digits), ct);
        }
        catch (SftpPermissionDeniedException)
        {
            // Not the account's to change: the file keeps the server's default.
        }
        catch (Exception ex) when (Map(ex, $"set the permissions of {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    // Whether a server's account may run sha256sum - asked once per server and account, not by every connection.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> CanHashOn = new(StringComparer.OrdinalIgnoreCase);
    private const string Probe = "dnnmanager-ok";

    // A shell answers the probe in milliseconds; an account forced into SFTP starts internal-sftp, which waits for input
    // and never answers - given up on after this, once per server.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private string HashKey => $"{connection.UserName}@{connection.Host}:{connection.Port}";

    public async Task<IReadOnlyDictionary<string, string>?> TryHashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (CanHashOn.TryGetValue(HashKey, out var known) && !known) return null;
        if (paths.Count == 0) return new Dictionary<string, string>();
        try
        {
            if (_ssh is null)
            {
                _ssh = new SshClient(Info());
                _ssh.HostKeyReceived += (_, e) => Check(e);
                await _ssh.ConnectAsync(ct);
            }
            if (!known)
            {
                // An account forced into SFTP (a chroot's internal-sftp) runs no command and says nothing - neither this.
                var probe = await RunAsync($"printf {Probe}; command -v sha256sum >/dev/null 2>&1 && printf ' sha256sum'", ProbeTimeout, ct);
                var can = probe.Trim() == $"{Probe} sha256sum";
                CanHashOn[HashKey] = can;
                if (!can) return null;
            }
            var byFull = paths.ToDictionary(Full, p => p, StringComparer.Ordinal);
            // Each path in single quotes - the shell takes nothing in it as a command; a quote in it closes, escapes and reopens.
            var output = await RunAsync("sha256sum -- " + string.Join(' ', byFull.Keys.Select(p => "'" + p.Replace("'", @"'\''") + "'")),
                TimeSpan.FromMinutes(5), ct);
            var hashes = new Dictionary<string, string>(DeployPath.Comparer);
            foreach (var line in output.Split('\n'))
            {
                // "<64 hex>  <path>" (or " *<path>" in binary mode); an escaped line (\ first) has a name we refuse anyway.
                if (line.Length < 67 || line[0] == '\\') continue;
                var hash = line[..64];
                if (!hash.All(Uri.IsHexDigit)) continue;
                var name = line[66..].TrimEnd('\r');
                if (byFull.TryGetValue(name, out var relative)) hashes[relative] = hash.ToLowerInvariant();
            }
            return hashes;
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException && !known)
        {
            // An account that may only use SFTP: the files are read back to hash them.
            CanHashOn[HashKey] = false;
            return null;
        }
        catch (Exception ex) when (Map(ex, "ask the server for hashes") is { } mapped)
        {
            throw mapped;
        }
    }

    private async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var run = _ssh!.CreateCommand(command);
        run.CommandTimeout = timeout;
        await run.ExecuteAsync(ct);
        return run.Result ?? "";
    }

    public async Task<bool> TryCreateAsync(string path, byte[] content, CancellationToken ct)
    {
        var full = Full(path);
        try
        {
            // Exclusive: the server refuses it when the file is there - two DNN Managers can't both make it.
            await using var stream = await Sftp.OpenAsync(full, FileMode.CreateNew, FileAccess.Write, ct);
            await stream.WriteAsync(content, ct);
            return true;
        }
        catch (SshException ex) when (ex is not SshConnectionException)
        {
            // Refused: it is there already - or it couldn't be made at all, which is said as it is.
            if (await ExistsAsync(full, ct)) return false;
            throw Map(ex, $"make {path}")!;
        }
        catch (Exception ex) when (Map(ex, $"make {path}") is { } mapped)
        {
            throw mapped;
        }
    }

    private async Task<bool> ExistsAsync(string full, CancellationToken ct)
    {
        try { return await Sftp.ExistsAsync(full, ct); }
        catch (SshException) { return false; }
    }

    private string Full(string relative) =>
        DeployPath.Normalize(relative) is { } safe ? DeployPath.Remote(_root, safe) : throw new RemoteException($"'{relative}' isn't a path DNN Manager uses.", false);

    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);

    private static int Mode(SftpFileAttributes a) =>
        (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0) |
        (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0) |
        (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    /// <summary>SSH.NET's exceptions as <see cref="RemoteException"/>s - null for what isn't one of them (it goes on as it is).</summary>
    private RemoteException? Map(Exception ex, string what) => ex switch
    {
        RemoteException => null,
        OperationCanceledException => null,
        SshAuthenticationException => new RemoteException($"Signing in to {connection.Host} as {connection.UserName} failed - check the user name, password or key.", false, ex),
        SftpPermissionDeniedException => new RemoteException($"Could not {what}: permission denied on the server.", false, ex),
        SftpPathNotFoundException => new RemoteException($"Could not {what}: not found on the server.", false, ex),
        SshPassPhraseNullOrEmptyException => new RemoteException("The private key needs its passphrase.", false, ex),
        SshConnectionException or SshOperationTimeoutException or SocketException or IOException or TimeoutException or ProxyException =>
            new RemoteException($"Could not {what}: {ex.Message}", true, ex),
        SftpException => new RemoteException($"Could not {what}: {ex.Message}", false, ex),
        SshException => new RemoteException($"Could not {what}: {ex.Message}", true, ex),
        ObjectDisposedException or InvalidOperationException when _disposed is false && _sftp is { IsConnected: false } =>
            new RemoteException($"Could not {what}: the connection was lost.", true, ex),
        _ => null
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in new BaseClient?[] { _sftp, _ssh })
        {
            if (client is null) continue;
            try
            {
                if (client.IsConnected) await Task.Run(client.Disconnect);
            }
            catch (Exception) { /* closing a broken connection */ }
            client.Dispose();
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the thread that raises it - not posted to a captured context.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
