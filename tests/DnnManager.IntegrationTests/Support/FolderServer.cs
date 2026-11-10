using System.Collections.Concurrent;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Deployment;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Deployment;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// A stand-in for an SFTP server: its folder is a folder on this PC. Its listing reports times to the second, as SFTP
/// does; it can compute hashes (like sha256sum) or not, and fail transfers on purpose - a network error a number of
/// times, or a refusal - to test retries, resuming and undo.
/// </summary>
public sealed class FolderServer
{
    public FolderServer(string root) => Root = Directory.CreateDirectory(root).FullName;

    public string Root { get; }

    /// <summary>Whether the server computes hashes itself (sha256sum) - otherwise files are read back.</summary>
    public bool CanHash { get; set; } = true;

    /// <summary>How often a transfer of a path fails with a network error before it works.</summary>
    public ConcurrentDictionary<string, int> FailTransient { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Uploads of these paths are refused, every time (permission denied).</summary>
    public ConcurrentDictionary<string, bool> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs before each upload, with the path sent to - someone else working on the server meanwhile.</summary>
    public Action<string>? BeforeUpload { get; set; }

    /// <summary>Folders the server doesn't let the account list.</summary>
    public ConcurrentDictionary<string, bool> Unlistable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An upload of these paths stops half-way with a network error, once - leaving its part on the server.</summary>
    public ConcurrentDictionary<string, bool> CutOnce { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was sent and from which offset (a resume starts after 0) - and how many files were read.</summary>
    public ConcurrentQueue<(string Path, long Offset)> Uploads { get; } = new();
    public ConcurrentQueue<string> Downloads { get; } = new();
    public int Connections;

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', '\\'));

    public void Write(string relative, string content, DateTime? modified = null)
    {
        var file = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        // A server's clock is its own: an edit there gets a time of its own, a second later than any before.
        File.SetLastWriteTimeUtc(file, modified ?? DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _tick)));
    }

    private static int _tick;

    /// <summary>Gives the file a time of its own, as a server's write does.</summary>
    public void Touch(string relative) => File.SetLastWriteTimeUtc(PathOf(relative), DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _tick)));

    public string? Read(string relative) => File.Exists(PathOf(relative)) ? File.ReadAllText(PathOf(relative)) : null;

    public IRemoteFileSystemFactory Factory => new ServerFactory(this);

    private sealed class ServerFactory(FolderServer server) : IRemoteFileSystemFactory
    {
        public IRemoteFileSystem Create(DeployConnection connection, DeploySecrets secrets) => new Session(server);
    }

    private sealed class Session(FolderServer server) : IRemoteFileSystem
    {
        public string? PresentedIdentity => "SHA256:test";
        public string? ServerDescription => "Folder";
        public bool CanResumeUpload => true;

        public Task ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref server.Connections);
            return Task.CompletedTask;
        }

        public Task<bool> RootExistsAsync(CancellationToken ct) => Task.FromResult(Directory.Exists(server.Root));

        public Task<IReadOnlyList<RemoteFile>> ListAsync(Func<string, bool> skipFolder, Action<string> warn, Action<string> unreadable, CancellationToken ct)
        {
            var files = new List<RemoteFile>();
            void Walk(string folder, string relative)
            {
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    var rel = relative.Length == 0 ? Path.GetFileName(dir) : relative + "/" + Path.GetFileName(dir);
                    if (server.Unlistable.ContainsKey(rel))
                    {
                        warn($"Left out {rel}/ on the server: no permission to list it.");
                        unreadable(rel + "/");
                        continue;
                    }
                    if (!skipFolder(rel)) Walk(dir, rel);
                }
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var rel = relative.Length == 0 ? Path.GetFileName(file) : relative + "/" + Path.GetFileName(file);
                    files.Add(Stat(rel)!);
                }
            }
            Walk(server.Root, "");
            return Task.FromResult<IReadOnlyList<RemoteFile>>(files);
        }

        private RemoteFile? Stat(string relative)
        {
            var file = server.PathOf(relative);
            if (!File.Exists(file)) return null;
            var info = new FileInfo(file);
            // To the second, as an SFTP server lists it.
            var time = info.LastWriteTimeUtc;
            return new RemoteFile(relative, info.Length, new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), 0x1A4);
        }

        public Task<RemoteFile?> StatAsync(string path, CancellationToken ct) => Task.FromResult(Stat(path));

        private void MaybeFail(string path)
        {
            if (server.FailTransient.TryGetValue(path, out var left) && left > 0)
            {
                server.FailTransient[path] = left - 1;
                throw new RemoteException($"Connection reset while sending {path}.", true);
            }
        }

        public async Task DownloadAsync(string path, Stream destination, long offset, Action<long>? progress, CancellationToken ct)
        {
            MaybeFail(path);
            server.Downloads.Enqueue(path);
            await using var file = File.OpenRead(server.PathOf(path));
            file.Seek(offset, SeekOrigin.Begin);
            await file.CopyToAsync(destination, ct);
        }

        public async Task UploadAsync(Stream source, string path, long offset, Action<long>? progress, CancellationToken ct)
        {
            var target = DeployPath.Name(path).EndsWith(DeployPath.PartSuffix) ? TargetOf(path) : path;
            if (server.Refuse.ContainsKey(target)) throw new RemoteException($"Could not send {target}: permission denied on the server.", false);
            MaybeFail(target);
            server.BeforeUpload?.Invoke(target);
            server.Uploads.Enqueue((path, offset));
            var file = server.PathOf(path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using var stream = new FileStream(file, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write);
            stream.Seek(offset, SeekOrigin.Begin);
            if (server.CutOnce.TryRemove(target, out _))
            {
                var half = new byte[Math.Max(1, (source.Length - offset) / 2)];
                var n = await source.ReadAsync(half, ct);
                await stream.WriteAsync(half.AsMemory(0, n), ct);
                throw new RemoteException($"Connection lost while sending {target}.", true);
            }
            await source.CopyToAsync(stream, ct);
        }

        // ".name.0123456789ab.dnnmgr-part" → "name", in the same folder.
        private static string TargetOf(string part)
        {
            var name = DeployPath.Name(part)[1..^DeployPath.PartSuffix.Length];
            name = name[..name.LastIndexOf('.')];
            var folder = DeployPath.Folder(part);
            return folder.Length == 0 ? name : folder + "/" + name;
        }

        public Task ReplaceAsync(string from, string to, CancellationToken ct)
        {
            var target = server.PathOf(to);
            File.Move(server.PathOf(from), target, true);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            if (File.Exists(server.PathOf(path))) File.Delete(server.PathOf(path));
            return Task.CompletedTask;
        }

        public Task DeleteEmptyFoldersAsync(string folder, CancellationToken ct)
        {
            for (var relative = folder; relative.Length > 0; relative = DeployPath.Folder(relative))
            {
                var dir = server.PathOf(relative);
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
            }
            return Task.CompletedTask;
        }

        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Task.CompletedTask;

        public async Task<IReadOnlyDictionary<string, string>?> TryHashAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            if (!server.CanHash) return null;
            var hashes = new Dictionary<string, string>(DeployPath.Comparer);
            foreach (var path in paths.Where(p => File.Exists(server.PathOf(p))))
                hashes[path] = await Hashing.OfFileAsync(server.PathOf(path), ct);
            return hashes;
        }

        public Task<bool> TryCreateAsync(string path, byte[] content, CancellationToken ct)
        {
            try
            {
                using var stream = new FileStream(server.PathOf(path), FileMode.CreateNew);
                stream.Write(content);
                return Task.FromResult(true);
            }
            catch (IOException) when (File.Exists(server.PathOf(path)))
            {
                return Task.FromResult(false);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A project, a server and DNN Manager's records of them, in a folder of the test's own - and the use cases run as the
/// operation runner runs them: in a scope of their own, undone when they fail or are cancelled.
/// </summary>
public sealed class DeployHarness : IDisposable
{
    /// <param name="real">A real server instead of the stand-in: its connection (saved with <paramref name="secrets"/>) and its factory.</param>
    public DeployHarness(DeployConnection? real = null, DeploySecrets? secrets = null, IRemoteFileSystemFactory? factory = null)
    {
        Folder = Path.Combine(Path.GetTempPath(), "dnnmanager-deploy-" + Guid.NewGuid().ToString("N")[..8]);
        Local = Directory.CreateDirectory(Path.Combine(Folder, "project")).FullName;
        Server = new FolderServer(Path.Combine(Folder, "server"));
        Factory = factory ?? Server.Factory;
        Database = new AppDatabase(Path.Combine(Folder, "data"));
        Store = new DeployStore(Database);
        Snapshots = new SnapshotStore(Path.Combine(Folder, "data", "deploy-snapshots"));
        Connection = new DeployConnection
        {
            Site = "shop", Name = "Live", Host = "example.test", UserName = "deploy", RemoteRoot = "/var/www/shop", TrustedHostKey = "SHA256:test",
            IgnoreRules = IgnoreRules.Defaults, Parallel = 3
        };
        if (real is not null) Connection = real;
        Store.Save(Connection);
        Store.SaveSecrets(Connection.Id, secrets ?? new DeploySecrets("secret"));
    }

    /// <summary>What reaches the server: the stand-in's, or the real one's.</summary>
    public IRemoteFileSystemFactory Factory { get; }

    /// <summary>A session of its own on the server - as someone editing it directly with their own client.</summary>
    public async Task<IRemoteFileSystem> OpenServerAsync()
    {
        var fs = Factory.Create(Connection, Store.ReadSecrets(Connection.Id));
        await fs.ConnectAsync(CancellationToken.None);
        return fs;
    }

    public string Folder { get; }
    public string Local { get; }
    public FolderServer Server { get; }
    public AppDatabase Database { get; }
    public DeployStore Store { get; }
    public SnapshotStore Snapshots { get; }
    public DeployConnection Connection { get; private set; }
    public RecordingReporter Reporter { get; } = new();

    public void Change(Func<DeployConnection, DeployConnection> change)
    {
        Connection = change(Connection);
        Store.Save(Connection);
    }

    public void WriteLocal(string relative, string content)
    {
        var file = Path.Combine(Local, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    public string? ReadLocal(string relative)
    {
        var file = Path.Combine(Local, relative.Replace('/', '\\'));
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    public void DeleteLocal(string relative) => File.Delete(Path.Combine(Local, relative.Replace('/', '\\')));

    public Task<SyncScan> ScanAsync(CancellationToken ct = default) =>
        new SyncScanner(Store, Snapshots, Factory, new LocalScanner(Store)).ScanAsync(Connection, Local, Reporter, ct);

    /// <summary>Runs <paramref name="work"/> as an operation: on a failure or a cancel its undo runs, as the runner's does.</summary>
    public async Task<Result<DeployOperation>> RunAsync(Func<SyncEngine, OperationUndo, Task<Result<DeployOperation>>> work, CancellationToken ct = default)
    {
        var undo = new OperationUndo();
        var engine = new SyncEngine(Snapshots, Factory, Store, undo);
        try
        {
            var result = await work(engine, undo);
            if (!result.Success) await undo.RunAsync(Reporter);
            return result;
        }
        catch (OperationCanceledException)
        {
            await undo.RunAsync(Reporter);
            return Result<DeployOperation>.Fail("Cancelled.");
        }
    }

    public Task<Result<DeployOperation>> PushAsync(SyncScan scan, params string[] paths) =>
        RunAsync((engine, undo) => new PushChangesUseCase(Store, Snapshots, Factory, engine, undo)
            .ExecuteAsync(new PushRequest(scan, paths, "Test push"), Reporter, CancellationToken.None));

    public Task<Result<DeployOperation>> PullAsync(SyncScan scan, params string[] paths) =>
        RunAsync((engine, undo) => new PullChangesUseCase(Store, Snapshots, Factory, engine, undo)
            .ExecuteAsync(new PullRequest(scan, paths, "Test pull"), Reporter, CancellationToken.None));

    public Task<Result<DeployOperation>> ResolveAsync(SyncScan scan, string path, ConflictChoice choice, byte[]? merged = null) =>
        RunAsync((engine, undo) => new ResolveConflictUseCase(Store, Snapshots, Factory, engine, undo)
            .ExecuteAsync(new ResolveRequest(scan, path, choice, merged), Reporter, CancellationToken.None));

    public Task<Result<DeployOperation>> RollbackAsync(long operation) =>
        RunAsync((engine, undo) => new RollbackOperationUseCase(Store, Snapshots, Factory, engine, undo)
            .ExecuteAsync(new RollbackRequest(Connection.Id, operation, Local, ""), Reporter, CancellationToken.None));

    public void Dispose()
    {
        try { Directory.Delete(Folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
