using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Which IIS site already answers a host name - checked before a new site is made with it.</summary>
public static class IisHostNames
{
    /// <summary>
    /// The site among <paramref name="sites"/> (by name) that already has an http binding for <paramref name="host"/> on
    /// <paramref name="port"/>; null when none has. IIS lets a second site have the same binding - and then doesn't start
    /// it, while requests for that name go to the first one, DNN's installer's too. The site named
    /// <paramref name="replacing"/> doesn't count: a new site of that name replaces it.
    /// </summary>
    public static string? SiteUsing(IEnumerable<KeyValuePair<string, IisSiteRuntime>> sites, string host, int port, string? replacing = null)
    {
        var name = host.Trim().TrimEnd('.');
        if (name.Length == 0) return null;
        return sites
            .Where(s => !string.Equals(s.Key, replacing, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(s => s.Value.Bindings.Any(b =>
                b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port == port &&
                b.Host.Trim().TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Key;
    }
}
