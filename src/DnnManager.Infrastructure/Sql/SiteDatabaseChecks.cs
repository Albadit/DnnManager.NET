using System.Collections.Concurrent;
using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Sql;

/// <summary>What a look at a site's database found: does its server answer, is the database on it, and why not.</summary>
public sealed record SiteDatabaseCheck(bool Reachable, bool Exists, string? Problem);

/// <summary>
/// Asks each site's SQL Server about its database - with the site's own web.config connection, never DNN Manager's
/// settings: once per server and login for the list of its databases, and the site's own database directly when that
/// list can't be read or doesn't have it (a login may not see the others). The Projects table (ServerStateMonitor) and
/// Host project's folders both ask this way. A server that doesn't answer is asked once more, not once per site: each
/// would be a connection waiting for the time-out on a thread of its own, every few seconds while it is down.
/// </summary>
public static class SiteDatabaseChecks
{
    // A server that doesn't answer is given this long - the sites are asked in parallel.
    public const int TimeoutSeconds = 5;

    /// <summary>What each of <paramref name="sites"/> (name → its web.config connection) found, by name.</summary>
    public static async Task<IReadOnlyDictionary<string, SiteDatabaseCheck>> AskAsync(ISqlConnectionTester tester,
        IReadOnlyDictionary<string, SiteSqlConnection> sites, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, SiteDatabaseCheck>(StringComparer.OrdinalIgnoreCase);

        async Task<SiteDatabaseCheck> AskDirectly(SiteSqlConnection site)
        {
            var test = await tester.TestAsync(site, ct, TimeoutSeconds);
            return test.Success ? new SiteDatabaseCheck(true, true, null) : new SiteDatabaseCheck(false, false, FirstLine(test.Error));
        }

        await Task.WhenAll(sites.GroupBy(s => (s.Value.Server, s.Value.User, s.Value.Password)).Select(async server =>
        {
            var list = await tester.ListDatabasesAsync(new SiteSqlConnection(server.Key.Server, "master", server.Key.User, server.Key.Password),
                ct, TimeoutSeconds);
            var databases = list.Success ? new HashSet<string>(list.Value!, StringComparer.OrdinalIgnoreCase) : null;
            await Task.WhenAll(server.Select(async site =>
            {
                if (databases is null) return;
                if (databases.Contains(site.Value.Database))
                    found[site.Key] = new SiteDatabaseCheck(true, true, null);
                else
                    found[site.Key] = await AskDirectly(site.Value) is { Reachable: true } direct ? direct
                        : new SiteDatabaseCheck(true, false, $"[{site.Value.Database}] isn't on {site.Value.Server}");
            }));
            if (databases is not null) return;

            // The list can't be read: one site is asked directly. Failing the same way, the server (or the login) is
            // the problem - every site of it has that answer. Otherwise its login may see only its own database (a
            // contained database user): each is asked.
            var sitesOfServer = server.ToList();
            var first = await AskDirectly(sitesOfServer[0].Value);
            found[sitesOfServer[0].Key] = first;
            if (!first.Reachable && first.Problem == FirstLine(list.Error))
                foreach (var site in sitesOfServer.Skip(1)) found[site.Key] = first;
            else
                await Task.WhenAll(sitesOfServer.Skip(1).Select(async site => found[site.Key] = await AskDirectly(site.Value)));
        }));
        return found;
    }

    private static string FirstLine(string? text) => (text ?? "it doesn't answer").Split('\n', 2)[0].Trim();
}
