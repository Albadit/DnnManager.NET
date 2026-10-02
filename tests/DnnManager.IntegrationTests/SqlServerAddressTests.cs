using DnnManager.Application.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>Whether a SQL Server address is this PC - a project's database is only dropped with it when it is.</summary>
[TestClass]
public sealed class SqlServerAddressTests
{
    [TestMethod]
    [DataRow("localhost,1433")]
    [DataRow("127.0.0.1,1433")]
    [DataRow(@".\SQLEXPRESS")]
    [DataRow("(local)")]
    [DataRow(@"(LocalDB)\MSSQLLocalDB")]
    [DataRow("tcp:localhost,1433")]
    [DataRow("[::1],1433")]
    public void AddressesOfThisPc_AreHere(string server) =>
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine(server), server);

    [TestMethod]
    public void ThisPcsName_IsHere() =>
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine($@"{Environment.MachineName}\SQLEXPRESS"));

    [TestMethod]
    [DataRow("staging-sql.example.com")]
    [DataRow("tcp:shop.database.windows.net,1433")]
    [DataRow("203.0.113.7,1433")]
    public void OtherServers_AreNot(string server) =>
        Assert.IsFalse(SqlServerAddress.IsOnThisMachine(server), server);

    [TestMethod]
    [DataRow("tcp:db.example.com,1433", "db.example.com")]
    [DataRow(@"SERVER\INSTANCE", "SERVER")]
    [DataRow("localhost", "localhost")]
    public void Host_IsTheServersNameAlone(string server, string host) =>
        Assert.AreEqual(host, SqlServerAddress.Host(server));
}
