using System.Net;
using System.Net.NetworkInformation;

namespace DnnManager.Application.Abstractions;

/// <summary>
/// Where a SQL Server address (<c>localhost,1433</c>, <c>.\SQLEXPRESS</c>, <c>tcp:db.example.com</c>) points: this PC,
/// or another server. What DNN Manager may do to a database without asking depends on it - a project's database is
/// dropped with the project only when it is here; one on a shared or remote server is someone else's to drop.
/// </summary>
public static class SqlServerAddress
{
    /// <summary>The host part: without <c>tcp:</c>/<c>np:</c>, the port (<c>,1433</c>) and the instance (<c>\SQLEXPRESS</c>).</summary>
    public static string Host(string server)
    {
        var host = server.Trim();
        var colon = host.IndexOf(':');
        if (colon > 0 && colon < 4 && !host.StartsWith('[')) host = host[(colon + 1)..]; // tcp:, np:, lpc:
        host = host.Split(',')[0].Split('\\')[0].Trim();
        return host.Trim('[', ']');
    }

    /// <summary>Whether <paramref name="server"/> is this PC: localhost, <c>.</c>, <c>(local)</c>, LocalDB, its name or one of its addresses.</summary>
    public static bool IsOnThisMachine(string server)
    {
        if (server.TrimStart().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return true;
        var host = Host(server);
        if (host is "" or "." or "(local)" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var machine = Environment.MachineName;
        if (host.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith(machine + ".", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
