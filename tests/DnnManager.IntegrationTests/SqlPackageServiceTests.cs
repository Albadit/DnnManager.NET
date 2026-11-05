using DnnManager.Infrastructure.Sql;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What SqlPackage's output says when an import fails: a contained database on a server with contained database
/// authentication off is recognised (and then switched on and imported again), and the message is SQL Server's own
/// error - not the last lines of the script SqlPackage ran.
/// </summary>
[TestClass]
public sealed class SqlPackageServiceTests
{
    // As SqlPackage 170 wrote it, importing a .bacpac of an Azure SQL database into the local SQL container.
    private const string ContainedOutput =
        "*** Error importing database:Could not import package.\r\n" +
        "Error SQL72014: Core Microsoft SqlClient Data Provider: Msg 12824, Level 16, State 1, Line 5 The sp_configure value " +
        "'contained database authentication' must be set to 1 in order to alter a contained database.  You may need to use " +
        "RECONFIGURE to set the value_in_use.\r\n" +
        "Error SQL72045: Script execution error.  The executed script:\r\n" +
        "IF EXISTS (SELECT 1\r\n" +
        "           FROM   [master].[dbo].[sysdatabases]\r\n" +
        "           WHERE  [name] = N'$(DatabaseName)')\r\n" +
        "    BEGIN\r\n" +
        "        ALTER DATABASE [$(DatabaseName)]\r\n" +
        "            SET CONTAINMENT = PARTIAL \r\n" +
        "            WITH ROLLBACK IMMEDIATE;\r\n" +
        "    END\r\n";

    [TestMethod]
    public void AContainedDatabase_IsRecognised()
    {
        Assert.IsTrue(SqlPackageService.NeedsContainedAuthentication(ContainedOutput));
        Assert.IsFalse(SqlPackageService.NeedsContainedAuthentication(
            "Error SQL72014: Core Microsoft SqlClient Data Provider: Msg 1801, Level 16, State 3, Line 1 Database 'shop' already exists."));
    }

    [TestMethod]
    public void TheMessage_IsSqlServersError_NotTheScriptsLastLines()
    {
        var message = SqlPackageService.ImportError(ContainedOutput, "");

        StringAssert.StartsWith(message, "Msg 12824, Level 16");
        StringAssert.Contains(message, "'contained database authentication' must be set to 1");
        Assert.IsFalse(message.Contains("ROLLBACK", StringComparison.Ordinal), message);
    }

    [TestMethod]
    public void WithoutASqlServerError_TheLastLinesAreTheMessage()
    {
        Assert.AreEqual("first | second | third", SqlPackageService.ImportError("", "zero\nfirst\nsecond\nthird\n"));
    }
}
