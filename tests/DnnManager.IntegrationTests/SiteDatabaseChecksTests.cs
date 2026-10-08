using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Sql;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Whether a site's database is live, as the Projects table and Host project's folders show it - asked with the site's
/// web.config connection, once per server and login.
/// </summary>
[TestClass]
public sealed class SiteDatabaseChecksTests
{
    [TestMethod]
    public async Task EachDatabase_IsLive_Missing_OrOffline()
    {
        var tester = new FakeTester();
        var sites = new Dictionary<string, SiteSqlConnection>(StringComparer.OrdinalIgnoreCase)
        {
            ["live"] = new("localhost,1433", "dnn_live", "sa", "secret"),
            ["missing"] = new("localhost,1433", "dnn_missing", "sa", "secret"),
            ["contained"] = new("localhost,1433", "dnn_contained", "sa", "secret"),
            ["offline"] = new("gone,1433", "dnn_gone", "sa", "secret")
        };

        var checks = await SiteDatabaseChecks.AskAsync(tester, sites, CancellationToken.None);

        Assert.AreEqual(new SiteDatabaseCheck(true, true, null), checks["live"]);
        Assert.AreEqual(new SiteDatabaseCheck(true, false, "[dnn_missing] isn't on localhost,1433"), checks["missing"]);
        // Not in the list its login sees, but it answers when asked directly.
        Assert.AreEqual(new SiteDatabaseCheck(true, true, null), checks["contained"]);
        Assert.AreEqual(new SiteDatabaseCheck(false, false, "A network-related error occurred."), checks["offline"]);
        // The list is read once per server and login, not once per site.
        Assert.AreEqual(2, tester.Lists);
    }

    [TestMethod]
    public async Task AServerThatDoesntAnswer_IsAskedOnce_NotOncePerSite()
    {
        var tester = new FakeTester();
        var sites = Enumerable.Range(0, 19).ToDictionary(i => $"site{i}", i => new SiteSqlConnection("gone,1433", $"dnn_{i}", "sa", "secret"),
            StringComparer.OrdinalIgnoreCase);

        var checks = await SiteDatabaseChecks.AskAsync(tester, sites, CancellationToken.None);

        Assert.IsTrue(checks.Values.All(c => c == new SiteDatabaseCheck(false, false, "A network-related error occurred.")));
        Assert.AreEqual(1, tester.Lists);
        Assert.AreEqual(1, tester.Tests, "Each connection to a server that is down waits for the time-out - once, not 19 times.");
    }

    [TestMethod]
    public async Task ALoginThatSeesOnlyItsOwnDatabase_IsAskedPerSite()
    {
        var tester = new FakeTester { ListFails = true };
        var sites = new Dictionary<string, SiteSqlConnection>(StringComparer.OrdinalIgnoreCase)
        {
            ["contained"] = new("localhost,1433", "dnn_contained", "user", "secret"),
            ["other"] = new("localhost,1433", "dnn_other", "user", "secret"),
        };

        var checks = await SiteDatabaseChecks.AskAsync(tester, sites, CancellationToken.None);

        Assert.AreEqual(new SiteDatabaseCheck(true, true, null), checks["contained"]);
        Assert.IsFalse(checks["other"].Reachable);
        Assert.AreEqual(2, tester.Tests, "The first answered otherwise than the list: each site is asked.");
    }

    private sealed class FakeTester : ISqlConnectionTester
    {
        private int _lists, _tests;
        public int Lists => _lists;
        public int Tests => _tests;

        /// <summary>The list can't be read on any server (a login that can't sign in to master).</summary>
        public bool ListFails { get; init; }

        public Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15)
        {
            Interlocked.Increment(ref _tests);
            return Task.FromResult(connection.Database == "dnn_contained"
                ? Result<string>.Ok("[dnn_contained] on SQL Server")
                : Result<string>.Fail("A network-related error occurred.\nMore detail."));
        }

        public Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15)
        {
            Interlocked.Increment(ref _lists);
            return Task.FromResult(server.Server == "gone,1433"
                ? Result<IReadOnlyList<string>>.Fail("A network-related error occurred.")
                : ListFails ? Result<IReadOnlyList<string>>.Fail("Login failed for user 'user'.")
                : Result<IReadOnlyList<string>>.Ok(["master", "dnn_live"]));
        }
    }
}
