using System.Text.Json;

namespace DnnManager.Infrastructure.Broker;

/// <summary>
/// The DNN Manager Broker - an experiment (.docs/privileged-broker.md): a Windows service that does the IIS work that
/// needs Administrator rights for a DNN Manager that runs without them. They talk over a named pipe, one request per
/// connection: one line of JSON each way.
/// </summary>
public static class BrokerProtocol
{
    public const string ServiceName = "DnnManagerBroker";
    public const string DisplayName = "DNN Manager Broker";
    public const string PipeName = "DnnManager.NET.Broker";

    /// <summary>
    /// Raised when a request or an answer changes in a way the other side must know: the service refuses a request of
    /// another version, so a DNN Manager and a service of different releases say so instead of misreading each other.
    /// </summary>
    public const int Version = 1;

    /// <summary>The longest request the service reads - an operation and a site name are far less.</summary>
    public const int MaxRequestBytes = 16 * 1024;

    /// <summary>The longest answer DNN Manager reads - every site's runtime of a busy IIS is far less.</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    public static class Operations
    {
        public const string Ping = "ping";
        public const string StartSite = "site.start";
        public const string StopSite = "site.stop";
        public const string StopSiteAndWait = "site.stop-and-wait";
        public const string RestartSite = "site.restart";
        public const string SiteStates = "sites.states";
        public const string SiteRuntimes = "sites.runtimes";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one message: the bytes up to the first line break. Null when the other side closed without sending one;
    /// <see cref="InvalidDataException"/> when it is longer than <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) return default;
            // One message per connection each way, so nothing that matters follows the line break.
            var end = Array.IndexOf(chunk, (byte)'\n', 0, read);
            message.Write(chunk, 0, end >= 0 ? end : read);
            if (message.Length > maxBytes) throw new InvalidDataException($"The message is longer than {maxBytes} bytes.");
            if (end >= 0) break;
        }
        return JsonSerializer.Deserialize<T>(message.ToArray(), Json);
    }
}

/// <param name="Version">The <see cref="BrokerProtocol.Version"/> the sender speaks.</param>
/// <param name="Operation">One of <see cref="BrokerProtocol.Operations"/>.</param>
/// <param name="Site">The IIS site the operation is about; none for the ones about every site.</param>
/// <param name="TimeoutSeconds">How long <see cref="BrokerProtocol.Operations.StopSiteAndWait"/> waits for the worker process.</param>
public sealed record BrokerRequest(int Version, string Operation, string? Site = null, int? TimeoutSeconds = null)
{
    public static BrokerRequest For(string operation, string? site = null, int? timeoutSeconds = null) =>
        new(BrokerProtocol.Version, operation, site, timeoutSeconds);
}

/// <param name="Data">The operation's answer (site states, runtimes…) as JSON; none for one that only succeeds or fails.</param>
public sealed record BrokerResponse(bool Success, string? Error = null, JsonElement? Data = null)
{
    public static BrokerResponse Ok() => new(true);

    public static BrokerResponse Ok<T>(T data) => new(true, null, JsonSerializer.SerializeToElement(data, BrokerProtocol.Json));

    public static BrokerResponse Fail(string error) => new(false, error);

    public T? DataAs<T>() => Data is { } data ? data.Deserialize<T>(BrokerProtocol.Json) : default;
}

/// <summary>The broker couldn't be asked: its service isn't running, or something else answered on its pipe.</summary>
public sealed class BrokerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
