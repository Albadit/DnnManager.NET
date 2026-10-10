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
}
