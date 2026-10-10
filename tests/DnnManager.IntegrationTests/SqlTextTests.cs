using DnnManager.Infrastructure.Docker;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;

namespace DnnManager.IntegrationTests;

/// <summary>
/// T-SQL text and connection strings made in one place each: names quoted, wildcards escaped, encryption decided the same
/// way for every connection - and what stops a cancelled BACKUP or RESTORE in SQL Server.
/// </summary>
[TestClass]
public sealed class SqlTextTests
{
    [TestMethod]
    [DataRow("shop", "[shop]")]
    [DataRow("a]b", "[a]]b]")]
    [DataRow("x]; DROP DATABASE y; --", "[x]]; DROP DATABASE y; --]")]
    public void Identifier_IsQuoted(string name, string quoted) => Assert.AreEqual(quoted, SqlText.Identifier(name));

    [TestMethod]
    public void Literal_DoublesQuotes() => Assert.AreEqual("O''Brien''s", SqlText.Literal("O'Brien's"));

    [TestMethod]
    public void EscapeLike_TakesWildcardsAsThemselves() => Assert.AreEqual("dnn[_]dev[%][[]x]", SqlText.EscapeLike("dnn_dev%[x]"));

    [TestMethod]
    public void ARemoteServer_MustShowACertificateWindowsTrusts()
    {
        var b = ConnectionStrings.For("sql.example.com", "shop", "dnn", "p;w='d", 30);
        Assert.AreEqual(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.IsFalse(b.TrustServerCertificate);
        Assert.AreEqual("p;w='d", b.Password);
        Assert.IsFalse(b.IntegratedSecurity);
        Assert.AreEqual(30, b.ConnectTimeout);
    }

    [TestMethod]
    public void ThisPc_IsEncrypted_WithItsOwnCertificate()
    {
        var b = ConnectionStrings.For("localhost,1433", "master", "sa", "x", 15);
        Assert.AreEqual(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.IsTrue(b.TrustServerCertificate);
    }

    [TestMethod]
    public void LocalDb_AndWindowsAuthentication()
    {
        var b = ConnectionStrings.For(@"(LocalDB)\MSSQLLocalDB", null, "", null, 60);
        Assert.AreEqual(SqlConnectionEncryptOption.Optional, b.Encrypt);
        Assert.IsTrue(b.IntegratedSecurity);
        Assert.AreEqual("", b.InitialCatalog);
    }

    [TestMethod]
    [DataRow(@"D:\SQL\Backup", true)]
    [DataRow(@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup", true)]
    [DataRow(@"\\fileserver\share\backups", false)]
    [DataRow(@"\\?\C:\backups", false)]
    [DataRow(@"backups", false)]
    [DataRow(@"C:backups", false)]
    [DataRow(@"C:\backups\..\Windows", false)]
    public void TheServersBackupFolder_IsOnlyUsedWhenItIsLocal(string folder, bool used) =>
        Assert.AreEqual(used, RemoteSqlBackupService.IsLocalFolder(folder), folder);

    [TestMethod]
    public void StoppingASession_KillsItByItsTag_AndCountsWhatIsLeft()
    {
        var tag = SqlServerService.SessionTag();
        StringAssert.StartsWith(tag, "dnnmanager-");
        Assert.AreNotEqual(tag, SqlServerService.SessionTag());

        var sql = SqlServerService.KillSessionSql(tag);
        StringAssert.Contains(sql, $"host_name = N'{tag}'");
        StringAssert.Contains(sql, "KILL ");
        StringAssert.Contains(sql, "session_id <> @@SPID");

        Assert.AreEqual(0, SqlServerService.SessionsLeft("LEFT=0\r\n"));
        Assert.AreEqual(1, SqlServerService.SessionsLeft("SPID 52: transaction rollback in progress.\r\nLEFT=1\r\n"));
        Assert.AreEqual(-1, SqlServerService.SessionsLeft("Sqlcmd: Error: login timeout expired"));
    }

    [TestMethod]
    public void ADropIsTriedAgain_OnlyWhileTheDatabaseIsInUse()
    {
        Assert.IsTrue(SqlServerService.IsInUse("Msg 3702, Level 16, State 3, Server x, Line 6\nCannot drop database \"a\" because it is currently in use."));
        Assert.IsTrue(SqlServerService.IsInUse("Msg 5061, Level 16, State 1"));
        Assert.IsFalse(SqlServerService.IsInUse("Msg 18456, Level 14, State 1\nLogin failed for user 'sa'."));
    }

    [TestMethod]
    [DataRow("dnn-sqlserver", true)]
    [DataRow("sql_2022.local", true)]
    [DataRow("--privileged", false)]
    [DataRow("-d", false)]
    [DataRow("C:/data", false)]
    [DataRow("../etc", false)]
    [DataRow("a b", false)]
    [DataRow("", false)]
    public void DockerNames_ThatCouldBeReadAsOptionsOrPaths_AreRefused(string name, bool valid) =>
        Assert.AreEqual(valid, DockerNames.IsValid(name), name);

    [TestMethod]
    public void TheContainer_RunsAPinnedImage_AndItsHealthCheckHasNoPasswordOnItsCommandLine()
    {
        var compose = new DockerComposeService(new DnnManager.Infrastructure.Processes.ProcessRunner())
            .Render(new DnnManager.Application.Configuration.DockerOptions());
        StringAssert.Contains(compose, "image: mcr.microsoft.com/mssql/server:2022-CU");
        StringAssert.Contains(compose, "@sha256:");
        Assert.IsFalse(compose.Contains("2022-latest", StringComparison.Ordinal));
        Assert.IsFalse(compose.Contains(" -P ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ALocalContainer_IsPublishedOnBothLoopbackAddresses_SoLocalhostAnswers()
    {
        // Windows resolves localhost to ::1 first: published on 127.0.0.1 only, SqlClient waited its whole timeout there.
        var local = new DnnManager.Application.Configuration.DockerOptions { ContainerIp = "localhost", DefaultPort = 1433 };
        CollectionAssert.AreEqual(new[] { "127.0.0.1:1433:1433", "[::1]:1433:1433" }, DockerComposeService.PublishedOn(local, ipv6: true).ToArray());
        CollectionAssert.AreEqual(new[] { "127.0.0.1:1433:1433" }, DockerComposeService.PublishedOn(local, ipv6: false).ToArray());
        var network = new DnnManager.Application.Configuration.DockerOptions { ContainerIp = "192.168.1.20", DefaultPort = 1433 };
        CollectionAssert.AreEqual(new[] { "1433:1433" }, DockerComposeService.PublishedOn(network, ipv6: true).ToArray());

        // Each address its own item of the ports list, at the list's indentation.
        var compose = new DockerComposeService(new DnnManager.Infrastructure.Processes.ProcessRunner()).Render(local);
        var items = compose.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains(":1433:1433\"", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(System.Net.Sockets.Socket.OSSupportsIPv6 ? 2 : 1, items.Count, compose);
        foreach (var item in items) StringAssert.StartsWith(item, "      - \"", compose);
    }

    [TestMethod]
    public async Task AVolumeWithTheOldDefaultPassword_IsSaidAtOnce_NotAfterMinutesOfWaiting()
    {
        var setup = new DnnManager.Application.UseCases.SetupSqlContainerUseCase(new ComposeThatStarts(), new OnlyOldPassword());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await setup.ExecuteAsync(new DnnManager.Application.Configuration.DockerOptions { SaPassword = "Th3-New-0ne!" },
            new DnnManager.IntegrationTests.Support.RecordingReporter(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Admin@123");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(30), $"It waited {watch.Elapsed.TotalSeconds:0} s.");
    }

    [TestMethod]
    public async Task AVolumeWithTheOldDefaultPassword_IsTakenIntoTheSettings_AndTheContainerMadeAgainWithIt()
    {
        var compose = new ComposeThatStarts();
        var setup = new DnnManager.Application.UseCases.SetupSqlContainerUseCase(compose, new OnlyOldPassword());
        string? adopted = null;

        var result = await setup.ExecuteAsync(new DnnManager.Application.Configuration.DockerOptions { SaPassword = "Th3-New-0ne!" },
            new DnnManager.IntegrationTests.Support.RecordingReporter(), CancellationToken.None,
            password => { adopted = password; return DnnManager.Domain.Result.Ok(); });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("Admin@123", adopted);
        // Made again with the volume's password: the container's health check signs in with it.
        CollectionAssert.AreEqual(new[] { "Th3-New-0ne!", "Admin@123" }, compose.Passwords);
    }

    [TestMethod]
    [DataRow("localhost,1433", "127.0.0.1,1433")]
    [DataRow("LOCALHOST, 1433", "127.0.0.1,1433")]
    [DataRow("tcp:localhost,14330", "tcp:127.0.0.1,14330")]
    [DataRow(@"localhost\SQLEXPRESS", @"localhost\SQLEXPRESS")]
    [DataRow("localhost", "localhost")]
    [DataRow("sql.example.com,1433", "sql.example.com,1433")]
    [DataRow(@"(LocalDB)\MSSQLLocalDB", @"(LocalDB)\MSSQLLocalDB")]
    public void Localhost_WithAPort_IsReachedAs127001(string server, string reached) =>
        // Windows tries localhost as ::1 first, and SqlClient waits its whole timeout there when the server listens on IPv4.
        Assert.AreEqual(reached, ConnectionStrings.Reachable(server));

    private sealed class ComposeThatStarts : DnnManager.Application.Abstractions.IDockerComposeService
    {
        public List<string> Passwords { get; } = [];

        public string Render(DnnManager.Application.Configuration.DockerOptions docker) => "";

        public Task<DnnManager.Domain.Result> UpAsync(DnnManager.Application.Configuration.DockerOptions docker,
            DnnManager.Application.Abstractions.IProgressReporter reporter, CancellationToken ct)
        {
            Passwords.Add(docker.SaPassword);
            return Task.FromResult(DnnManager.Domain.Result.Ok());
        }
    }

    /// <summary>A SQL Server whose sa still has the password DNN Manager 1.7.1 and older gave it.</summary>
    private sealed class OnlyOldPassword : DnnManager.Application.Abstractions.ISqlConnectionTester
    {
        public Task<DnnManager.Domain.Result<string>> TestAsync(DnnManager.Application.Abstractions.SiteSqlConnection connection, CancellationToken ct,
            int timeoutSeconds = 15) =>
            Task.FromResult(connection.Password == "Admin@123"
                ? DnnManager.Domain.Result<string>.Ok("SQL Server 2022")
                : DnnManager.Domain.Result<string>.Fail("Login failed for user 'sa'."));

        public Task<DnnManager.Domain.Result<IReadOnlyList<string>>> ListDatabasesAsync(DnnManager.Application.Abstractions.SiteSqlConnection server,
            CancellationToken ct, int timeoutSeconds = 15) =>
            Task.FromResult(DnnManager.Domain.Result<IReadOnlyList<string>>.Ok([]));
    }
}
