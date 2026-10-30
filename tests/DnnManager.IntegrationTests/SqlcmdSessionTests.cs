using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Sql;
using DnnManager.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What runs through sqlcmd on a real SQL Server, in a container of the test's own: a database restored from a live DNN
/// site has filtered indexes, and SQL Server refuses to change their tables under sqlcmd's own session settings.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class SqlcmdSessionTests
{
    private const string Database = "dnnit_ssl";

    private static string? _name, _server, _password;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        if (!TestEnvironment.DockerAvailable()) return;
        _name = "dnnit-sqlcmd-" + Guid.NewGuid().ToString("N")[..8];
        _password = TestEnvironment.Password(24, "-_");
        var port = Enumerable.Range(14400, 60).First(p => !IisExpressSites.PortOpen(p));
        _server = await TestEnvironment.StartSqlContainerAsync(_name, port, _password);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (_name is not null) TestEnvironment.RemoveContainer(_name);
    }

    [TestMethod]
    public async Task TurningSslOff_ChangesTabs_ThoughItHasAFilteredIndex()
    {
        if (_server is null) Assert.Inconclusive("Docker with Linux containers isn't available for a SQL Server container.");

        await using var conn = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = _server, InitialCatalog = "master", UserID = "sa", Password = _password,
            Encrypt = true, TrustServerCertificate = true, Pooling = false
        }.ConnectionString);
        await conn.OpenAsync();
        await Exec(conn, $"CREATE DATABASE [{Database}]");
        conn.ChangeDatabase(Database);
        // As DNN's: Tabs.IX_Tabs_ContentID is filtered.
        await Exec(conn, """
            CREATE TABLE dbo.PortalSettings (PortalID int NOT NULL, SettingName nvarchar(50) NOT NULL, SettingValue nvarchar(max) NULL);
            CREATE TABLE dbo.Tabs (TabID int NOT NULL PRIMARY KEY, ContentID int NULL, IsSecure bit NOT NULL);
            CREATE INDEX IX_Tabs_ContentID ON dbo.Tabs (ContentID) WHERE ContentID IS NOT NULL;
            INSERT INTO dbo.PortalSettings VALUES (0, N'SSLEnabled', N'True');
            INSERT INTO dbo.Tabs VALUES (1, 10, 1), (2, 20, 0);
            """);

        var sql = new SqlServerService(new ProcessRunner(), Options.Create(new AppOptions
        {
            Docker = new DockerOptions { ContainerName = _name!, SaPassword = _password! }
        }));
        var changed = await sql.DisableSslAsync(Database, CancellationToken.None);

        Assert.IsTrue(changed.Success, changed.Error);
        Assert.AreEqual(2, changed.Value, "One setting and one secure page.");
        Assert.AreEqual(0, await Scalar(conn, "SELECT COUNT(*) FROM dbo.Tabs WHERE IsSecure = 1"));
        Assert.AreEqual(0, await Scalar(conn, "SELECT COUNT(*) FROM dbo.PortalSettings WHERE SettingValue = N'True'"));
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> Scalar(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
