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

    private sealed class FakeTester : ISqlConnectionTester
    {
        private int _lists;
        public int Lists => _lists;

        public Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15) =>
            Task.FromResult(connection.Database == "dnn_contained"
                ? Result<string>.Ok("[dnn_contained] on SQL Server")
                : Result<string>.Fail("A network-related error occurred.\nMore detail."));

        public Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15)
        {
            Interlocked.Increment(ref _lists);
            return Task.FromResult(server.Server == "gone,1433"
                ? Result<IReadOnlyList<string>>.Fail("A network-related error occurred.")
                : Result<IReadOnlyList<string>>.Ok(["master", "dnn_live"]));
        }
    }
}
