using System.Net;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.KeepWarm;

/// <summary>
/// Where a keep-warm request to a site goes: this machine, on the binding a browser opens the site at, with that
/// binding's host name in the Host header - IIS routes it to the site whatever DNS says, and DNN finds its portal alias.
/// </summary>
/// <param name="Scheme">"http" or "https".</param>
/// <param name="ConnectHost">The address connected to: 127.0.0.1 for a binding on every address, else the binding's own.</param>
/// <param name="HostHeader">The host name, with the port when it isn't the scheme's own - e.g. <c>mysite.dnndev.me</c>.</param>
public sealed record LocalSiteTarget(string Scheme, string ConnectHost, int Port, string HostHeader)
{
    /// <summary>The address a request for <paramref name="pathAndQuery"/> is sent to.</summary>
    public Uri UriFor(string pathAndQuery) => new($"{Scheme}://{ConnectHost}:{Port}{pathAndQuery}");

    /// <summary>The page as people know it: <c>http://mysite.dnndev.me/KeepAlive.aspx</c>.</summary>
    public string DisplayUrl(string pathAndQuery) => $"{Scheme}://{HostHeader}{pathAndQuery}";

    /// <summary>
    /// The target for <paramref name="site"/>: its bindings in the order <see cref="IisSiteRuntime.BrowseUrl"/> takes
    /// them, leaving out those that have no port or a wildcard host name (<c>*.example.com</c> is no Host header). Null
    /// when none is left.
    /// </summary>
    public static LocalSiteTarget? For(IisSiteRuntime site)
    {
        var binding = site.Bindings
            .Where(b => b.IsWeb && b.Port is not null && !b.Host.Contains('*'))
            .OrderBy(b => (b.IsHttps && b.HasCertificate ? 0 : b.IsHttps ? 2 : 1) + (b.Host.Length > 0 ? 0 : 3))
            .FirstOrDefault();
        if (binding is null) return null;

        var scheme = binding.IsHttps ? "https" : "http";
        var port = binding.Port!.Value;
        var host = binding.Host.Length > 0 ? binding.Host : "localhost";
        var hostHeader = port == (binding.IsHttps ? 443 : 80) ? host : $"{host}:{port}";
        return new LocalSiteTarget(scheme, ConnectAddress(binding.Address), port, hostHeader);
    }

    /// <summary>The address to connect to for a binding on <paramref name="address"/>: loopback for all of them.</summary>
    private static string ConnectAddress(string address)
    {
        var a = address.Trim();
        if (a.Length == 0 || a == "*") return "127.0.0.1";
        // IIS writes an IPv6 address in brackets already; a bare one gets them here.
        if (IPAddress.TryParse(a.Trim('[', ']'), out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        return "127.0.0.1";
    }
}

/// <summary>
/// How a site is kept warm: Settings → Projects → Keep warm, and its IIS app pool's idle time-out - what the service
/// requests, and what the site's overview shows.
/// </summary>
/// <param name="Interval">The time between two requests at most.</param>
/// <param name="IdleTimeout">The app pool's idle time-out; null when it isn't known.</param>
/// <param name="Target">Where requests go; null when the site has no binding to request.</param>
public sealed record KeepWarmPlan(
    int PingMinutes,
    TimeSpan Interval,
    TimeSpan? IdleTimeout,
    string WarmUpPath,
    string PingPath,
    LocalSiteTarget? Target)
{
    public static KeepWarmPlan For(KeepWarmSettings settings, IisSiteRuntime site) =>
        new(settings.PingMinutes, KeepWarmRules.Interval(settings.PingMinutes, site.IdleTimeout), site.IdleTimeout,
            Fallback(settings.WarmUpPath, KeepWarmSettings.DefaultWarmUpPath),
            Fallback(settings.PingPath, KeepWarmSettings.DefaultPingPath),
            LocalSiteTarget.For(site));

    // A page that can't be requested (never one of the installer's) - the built-in one instead.
    private static string Fallback(string path, string builtIn) =>
        KeepWarmSettings.PathProblem(path) is null ? KeepWarmSettings.NormalizePath(path) : builtIn;
}
