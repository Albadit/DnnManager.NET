using System.Net;
using System.Net.NetworkInformation;

namespace DnnManager.Application.Abstractions;

/// <summary>
/// A SQL Server address as a connection string writes it (<c>localhost,1433</c>, <c>.\SQLEXPRESS</c>,
/// <c>tcp:db.example.com</c>, <c>[::1],1433</c>, <c>(localdb)\MSSQLLocalDB</c>), taken apart - the one place that reads
/// one. Where it points decides what DNN Manager may do to a database without asking: a project's database is dropped
/// with the project only when it is here; one on a shared or remote server is someone else's to drop.
/// </summary>
/// <param name="Protocol">The prefix without its colon (<c>tcp</c>, <c>np</c>, <c>lpc</c>), empty when there is none.</param>
/// <param name="Host">The server's name or address alone: no prefix, port, instance or IPv6 brackets.</param>
/// <param name="Port">The port after the comma; null when none is given.</param>
/// <param name="Instance">The instance after the backslash (<c>SQLEXPRESS</c>); empty for the default instance.</param>
/// <param name="IsLocalDb">A LocalDB instance: <c>(localdb)\…</c>.</param>
public sealed record SqlServerAddress(string Protocol, string Host, int? Port, string Instance, bool IsLocalDb)
{
    /// <summary>The port SQL Server's default instance listens on.</summary>
    public const int DefaultPort = 1433;

    public static SqlServerAddress Parse(string? server)
    {
        var s = (server ?? "").Trim();
        if (s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            var slash = s.IndexOf('\\');
            return new SqlServerAddress("", "(localdb)", null, slash < 0 ? "" : s[(slash + 1)..].Trim(), true);
        }

        var protocol = "";
        var colon = s.IndexOf(':');
        if (colon > 0 && colon < 6 && !s.StartsWith('[') && s[..colon].All(char.IsLetter))
        {
            protocol = s[..colon].ToLowerInvariant();
            s = s[(colon + 1)..].Trim();
        }

        // A named pipe: np:\\server\pipe\… - or a pipe's path alone (\\server\pipe\…), which SqlClient opens as one too: its
        // server is the part after the two backslashes, not an empty host (which would be this PC).
        if (protocol == "np" || s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            protocol = "np";
            var path = s.TrimStart('\\');
            var end = path.IndexOf('\\');
            return new SqlServerAddress(protocol, end < 0 ? path : path[..end], null, "", false);
        }

        string host, rest;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            host = close < 0 ? s.Trim('[') : s[1..close];
            rest = close < 0 ? "" : s[(close + 1)..];
        }
        else
        {
            var cut = s.IndexOfAny([',', '\\']);
            host = cut < 0 ? s : s[..cut];
            rest = cut < 0 ? "" : s[cut..];
        }

        var instance = "";
        int? port = null;
        var comma = rest.IndexOf(',');
        var instancePart = comma < 0 ? rest : rest[..comma];
        if (instancePart.StartsWith('\\')) instance = instancePart[1..].Trim();
        if (comma >= 0 && int.TryParse(rest[(comma + 1)..].Trim(), out var p)) port = p;
        return new SqlServerAddress(protocol, host.Trim(), port, instance, false);
    }

    /// <summary>The host part: without <c>tcp:</c>/<c>np:</c>, the port (<c>,1433</c>) and the instance (<c>\SQLEXPRESS</c>).</summary>
    public static string HostOf(string server) => Parse(server).Host;

    /// <summary>Whether <paramref name="server"/> is this PC: localhost, <c>.</c>, <c>(local)</c>, LocalDB, its name or one of its addresses.</summary>
    public static bool IsOnThisMachine(string server) => Parse(server).OnThisMachine;

    /// <summary>This PC: localhost, <c>.</c>, <c>(local)</c>, LocalDB, its name or one of its addresses.</summary>
    public bool OnThisMachine => IsLocalDb || IsLocalHost(Host);

    /// <summary>
    /// Where a TCP connection reaches it: the host (<c>.</c> and <c>(local)</c> as 127.0.0.1) and the port (1433 when
    /// none is given). Null for what isn't reached that way - a named instance, LocalDB, a pipe.
    /// </summary>
    public (string Host, int Port)? TcpEndpoint
    {
        get
        {
            if (IsLocalDb || Instance.Length > 0 || Protocol is "np" or "lpc") return null;
            var host = Host is "." or "(local)" ? "127.0.0.1" : Host;
            return host.Length == 0 ? null : (host, Port ?? DefaultPort);
        }
    }

    /// <summary>
    /// The shared SQL Server container at <paramref name="containerHost"/> publishing <paramref name="publishedPort"/>:
    /// a default instance on this PC (or at that host), at that port - an address without one means 1433, so it is the
    /// container only when that is the port it publishes.
    /// </summary>
    public bool IsContainer(string containerHost, int publishedPort) =>
        IsContainerHost(containerHost) && (Port ?? DefaultPort) == publishedPort;

    /// <summary>The container's host, on whatever port: its address from the settings, or this PC - default instance only.</summary>
    public bool IsContainerHost(string containerHost) =>
        !IsLocalDb && Instance.Length == 0 && Protocol is "" or "tcp" &&
        (Host.Equals(containerHost.Trim(), StringComparison.OrdinalIgnoreCase) || IsLocalHost(Host));

    /// <summary>
    /// The same SQL Server as <paramref name="other"/>, however each writes it (<c>.</c>, <c>localhost,1433</c> and this
    /// PC's name are one server; so are <c>.\SQLEXPRESS</c> and <c>(local)\sqlexpress</c>).
    /// </summary>
    public bool SameServerAs(SqlServerAddress other)
    {
        if (IsLocalDb || other.IsLocalDb)
            return IsLocalDb && other.IsLocalDb && Instance.Equals(other.Instance, StringComparison.OrdinalIgnoreCase);
        if (!Instance.Equals(other.Instance, StringComparison.OrdinalIgnoreCase)) return false;
        // A named instance without a port is found through SQL Browser: the instance says which one it is.
        var samePort = Instance.Length > 0 && Port is null && other.Port is null ||
                       (Port ?? DefaultPort) == (other.Port ?? DefaultPort);
        if (!samePort) return false;
        return OnThisMachine && other.OnThisMachine || Host.Equals(other.Host, StringComparison.OrdinalIgnoreCase);
    }

    public bool SameServerAs(string other) => SameServerAs(Parse(other));

    /// <summary>
    /// Whether DNN Manager may sign in to <paramref name="server"/> with the user's Windows account without asking: a
    /// server on this PC, or the SQL Server chosen in Settings → Database server (<paramref name="chosen"/>) - not just
    /// any a site's web.config names, which the site's app pool can change: Windows authentication would hand that server
    /// the user's sign-in. The monitor leaves any other unasked; an operation the user starts asks first, naming it.
    /// </summary>
    public static bool MaySignInAsUser(string server, Configuration.DatabaseServerOptions chosen)
    {
        if (IsOnThisMachine(server)) return true;
        return !chosen.IsContainer && !chosen.IsLocalDbFile && chosen.Server.Length > 0 && Parse(server).SameServerAs(Parse(chosen.Server));
    }

    /// <summary>The question asked before signing in to <paramref name="server"/> as the user, when <see cref="MaySignInAsUser"/> isn't so.</summary>
    public static string SignInQuestion(string server, string why) =>
        $"{why} signs in to {server} with your Windows account. That server is named by the site's web.config - not on this PC, " +
        "and not the one in Settings → Database server - and gets your Windows sign-in.\n\nSign in to it as you?";

    private static bool IsLocalHost(string host)
    {
        if (host is "" or "." or "(local)" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // Its name: the NetBIOS one (at most 15 characters) or its full DNS name - not any name that merely starts with it
        // (PC.example.com is someone else's server, whose database DNN Manager would otherwise treat as its own).
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        var local = LocalNames.Current;
        if (local.Names.Contains(host)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        return IPAddress.IsLoopback(address) || local.Addresses.Contains(address);
    }

    /// <summary>
    /// This PC's DNS names and addresses - read once, and again only when an address changes (a network joined or left):
    /// the monitor asks every few seconds, for every site, and reading every adapter each time costs.
    /// </summary>
    internal sealed class LocalNames
    {
        private static volatile LocalNames? _current;

        static LocalNames()
        {
            // Read again the next time it is asked, after a change.
            NetworkChange.NetworkAddressChanged += (_, _) => _current = null;
        }

        private LocalNames(HashSet<string> names, HashSet<IPAddress> addresses) { Names = names; Addresses = addresses; }

        public HashSet<string> Names { get; }
        public HashSet<IPAddress> Addresses { get; }

        public static LocalNames Current => _current ??= Read();

        /// <summary>Forgets what was read - as an address change does (for the tests).</summary>
        internal static void Forget() => _current = null;

        /// <summary>Whether it is read already, and kept.</summary>
        internal static bool IsKept => _current is not null;

        private static LocalNames Read()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new HashSet<IPAddress>();
            try
            {
                var dns = IPGlobalProperties.GetIPGlobalProperties();
                if (dns.HostName.Length > 0) names.Add(dns.HostName);
                if (dns.HostName.Length > 0 && dns.DomainName.Length > 0) names.Add($"{dns.HostName}.{dns.DomainName}");
            }
            catch (NetworkInformationException)
            {
                // Not known: only the machine's name.
            }
            try
            {
                foreach (var address in NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses))
                    addresses.Add(address.Address);
            }
            catch (NetworkInformationException)
            {
                // None known: no address counts as this PC's but the loopback ones.
            }
            return new LocalNames(names, addresses);
        }
    }
}
