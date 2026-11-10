using System.Security.Cryptography;

namespace DnnManager.Application.Deployment;

/// <summary>SHA-256, as lowercase hex - how every file of a deployment is compared, checked and kept.</summary>
public static class Hashing
{
    public static string ToHex(byte[] hash) => Convert.ToHexStringLower(hash);

    public static async Task<string> OfStreamAsync(Stream stream, CancellationToken ct) => ToHex(await SHA256.HashDataAsync(stream, ct));

    public static string OfBytes(byte[] bytes) => ToHex(SHA256.HashData(bytes));

    /// <summary>The hash of <paramref name="file"/> - read while others may write it (a site's log, say).</summary>
    public static async Task<string> OfFileAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await OfStreamAsync(stream, ct);
    }

    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A stream that is read once, start to end, and hashed as it is read: when <paramref name="expected"/> is given, its end
/// throws <see cref="InvalidDataException"/> when the bytes weren't that content - nothing that reads it to the end
/// can take changed content for the one it expects. <see cref="Hash"/> is the hash once the end is reached.
/// </summary>
public sealed class HashingStream(Stream inner, string? expected = null, bool leaveOpen = false) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _result;
    private long _read;

    /// <summary>The hash of what was read - null until the end was reached.</summary>
    public string? Hash => _result;

    public long BytesRead => _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        Seen(buffer[..n], n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        Seen(buffer.Span[..n], n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Seen(ReadOnlySpan<byte> bytes, int n)
    {
        if (n > 0)
        {
            _hash.AppendData(bytes);
            _read += n;
            return;
        }
        if (_result is not null) return;
        _result = Hashing.ToHex(_hash.GetHashAndReset());
        if (expected is not null && !Hashing.Same(_result, expected))
            throw new InvalidDataException($"The content changed: it should be {Short(expected)}, but is {Short(_result)}.");
    }

    private static string Short(string hash) => hash[..Math.Min(12, hash.Length)];

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!leaveOpen) inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        if (!leaveOpen) await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>A stream that only hashes what is written to it - to check a file on the server by reading it back.</summary>
public sealed class HashSink : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    /// <summary>The hash of everything written - asked once, at the end.</summary>
    public string Finish() => Hashing.ToHex(_hash.GetHashAndReset());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _length += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
