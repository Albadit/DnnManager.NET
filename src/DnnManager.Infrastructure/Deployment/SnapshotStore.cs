using DnnManager.Application;
using DnnManager.Application.Deployment;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.Infrastructure.Deployment;

/// <summary>
/// File contents by their SHA-256, in <c>Documents\DnnManager\deploy-snapshots\objects\ab\abcdef…</c> (as git keeps
/// objects): the same content is kept once, whatever file and operation it was. The folder is the user's - every program
/// of theirs can change it - so a content is only ever read back checked against its name (<see cref="OpenVerified"/>),
/// and nothing is written through a link or junction there.
/// </summary>
public sealed class SnapshotStore : ISnapshotStore
{
    private readonly string _root;

    public SnapshotStore(AppDataPaths paths) : this(paths.DeploySnapshotsDirectory) { }

    public SnapshotStore(string root) => _root = root;

    private string Objects => Path.Combine(_root, "objects");
    private string Incoming => Path.Combine(_root, "incoming");

    private string? PathOf(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
        var lower = hash.ToLowerInvariant();
        return Path.Combine(Objects, lower[..2], lower);
    }

    public bool Contains(string hash) => PathOf(hash) is { } file && File.Exists(file);

    public long? SizeOf(string hash) => PathOf(hash) is { } file && File.Exists(file) ? new FileInfo(file).Length : null;

    public async Task<string> AddAsync(Stream content, CancellationToken ct)
    {
        await using var writer = BeginAdd();
        await content.CopyToAsync(writer.Stream, ct);
        return await writer.CompleteAsync(_ => true, ct);
    }

    public async Task<string> AddFileAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await AddAsync(stream, ct);
    }

    public ISnapshotWriter BeginAdd()
    {
        Directory.CreateDirectory(Incoming);
        SafePath.EnsureNoLink(_root, Incoming);
        return new Writer(this, Path.Combine(Incoming, Guid.NewGuid().ToString("N")));
    }

    public Stream? OpenVerified(string hash)
    {
        if (PathOf(hash) is not { } file || !File.Exists(file)) return null;
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new HashingStream(stream, hash);
    }

    public async Task<byte[]?> ReadAsync(string hash, long maxBytes, CancellationToken ct)
    {
        if (PathOf(hash) is not { } file || !File.Exists(file) || new FileInfo(file).Length > maxBytes) return null;
        var bytes = await File.ReadAllBytesAsync(file, ct);
        return Hashing.Same(Hashing.OfBytes(bytes), hash) ? bytes : null;
    }

    public int Prune(IReadOnlySet<string> keep, TimeSpan minAge)
    {
        var before = DateTime.UtcNow - minAge;
        var forgotten = 0;
        if (Directory.Exists(Incoming))
            foreach (var file in Directory.EnumerateFiles(Incoming, "*", SafePath.TopLevel))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) TryDelete(file);
        if (!Directory.Exists(Objects) || SafePath.IsLink(_root) || SafePath.IsLink(Objects)) return 0;
        foreach (var folder in Directory.EnumerateDirectories(Objects, "*", SafePath.TopLevel))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SafePath.TopLevel))
            {
                if (keep.Contains(Path.GetFileName(file)) || File.GetLastWriteTimeUtc(file) >= before) continue;
                if (TryDelete(file)) forgotten++;
            }
            try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return forgotten;
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A content written to <c>incoming</c>, hashed as it is written, and moved into <c>objects</c> when complete.</summary>
    private sealed class Writer(SnapshotStore store, string temp) : ISnapshotWriter
    {
        private readonly FileStream _file = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        private readonly System.Security.Cryptography.IncrementalHash _hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        private HashingWriteStream? _stream;
        private bool _done;

        public Stream Stream => _stream ??= new HashingWriteStream(_file, _hash);

        public async Task<string> CompleteAsync(Func<string, bool> keep, CancellationToken ct)
        {
            await _file.FlushAsync(ct);
            await _file.DisposeAsync();
            _done = true;
            var hash = Hashing.ToHex(_hash.GetHashAndReset());
            if (!keep(hash) || store.Contains(hash))
            {
                TryDelete(temp);
                return hash;
            }
            var target = store.PathOf(hash)!;
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            SafePath.EnsureNoLink(store._root, folder);
            try
            {
                File.Move(temp, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Kept meanwhile by a transfer running in parallel - the same content.
                TryDelete(temp);
            }
            return hash;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_done)
            {
                await _file.DisposeAsync();
                TryDelete(temp);
            }
            _hash.Dispose();
        }
    }

    /// <summary>Writes to the file and the hash at once.</summary>
    private sealed class HashingWriteStream(FileStream file, System.Security.Cryptography.IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        public override void Flush() => file.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            file.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await file.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // The file is the writer's to close.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}
