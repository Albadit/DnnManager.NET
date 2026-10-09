using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>One of a site's IIS bindings.</summary>
/// <param name="Protocol">"http", "https", or another (net.tcp…) that browsers don't use.</param>
/// <param name="Address">The IP address it listens on; "*" for all of them.</param>
/// <param name="Port">Null for a binding that isn't "address:port:host" (net.pipe…).</param>
/// <param name="Host">The host name it answers to; empty for any.</param>
/// <param name="HasCertificate">An https binding with an SSL certificate assigned.</param>
public sealed record IisBinding(string Protocol, string Address, int? Port, string Host, bool HasCertificate)
{
    public bool IsHttps => Protocol.Equals("https", StringComparison.OrdinalIgnoreCase);
    public bool IsWeb => IsHttps || Protocol.Equals("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The address a browser opens it at - its host name (localhost when it answers any), with the port when it
    /// isn't the protocol's own. Null when it isn't a web binding.
    /// </summary>
    public string? Url
    {
        get
        {
            if (!IsWeb || Port is not { } port) return null;
            var scheme = IsHttps ? "https" : "http";
            var host = Host.Length > 0 ? Host : "localhost";
            return port == (IsHttps ? 443 : 80) ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
        }
    }

    public override string ToString() => $"{Protocol} {Address}:{Port}:{Host}";
}
