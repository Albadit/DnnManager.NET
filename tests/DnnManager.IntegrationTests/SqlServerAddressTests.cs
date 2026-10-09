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
    [DataRow("[::1],1433", "::1")]
    [DataRow(@"np:\\SQLBOX\pipe\sql\query", "SQLBOX")]
    public void Host_IsTheServersNameAlone(string server, string host) =>
        Assert.AreEqual(host, SqlServerAddress.HostOf(server));

    [TestMethod]
    [DataRow("localhost,1433", 1433, true)]
    [DataRow("localhost", 1433, true)]
    [DataRow(".", 1433, true)]
    [DataRow("127.0.0.1,14330", 14330, true)]
    // No port is 1433: the container only when that is the port it publishes.
    [DataRow("localhost", 14330, false)]
    [DataRow(".", 14330, false)]
    [DataRow("localhost,1433", 14330, false)]
    // A named instance or LocalDB is never the container.
    [DataRow(@".\SQLEXPRESS", 1433, false)]
    [DataRow(@"(localdb)\MSSQLLocalDB", 1433, false)]
    [DataRow("staging-sql.example.com,1433", 1433, false)]
    public void TheContainer_IsTheDefaultInstanceHereAtItsPort(string server, int publishedPort, bool container) =>
        Assert.AreEqual(container, SqlServerAddress.Parse(server).IsContainer("localhost", publishedPort), server);

    [TestMethod]
    public void TheContainer_IsAlsoAtTheHostFromTheSettings() =>
        Assert.IsTrue(SqlServerAddress.Parse("10.0.0.5,1433").IsContainer("10.0.0.5", 1433));

    [TestMethod]
    [DataRow(".", "localhost,1433")]
    [DataRow("(local)", "127.0.0.1")]
    [DataRow(@".\SQLEXPRESS", @"localhost\sqlexpress")]
    [DataRow(@"(localdb)\MSSQLLocalDB", @"(LocalDB)\mssqllocaldb")]
    [DataRow("tcp:db.example.com,1433", "DB.example.com")]
    public void OneServer_WrittenTwoWays_IsTheSame(string a, string b) =>
        Assert.IsTrue(SqlServerAddress.Parse(a).SameServerAs(b), $"{a} / {b}");

    [TestMethod]
    [DataRow(".", @".\SQLEXPRESS")]
    [DataRow("localhost,1433", "localhost,14330")]
    [DataRow("localhost", "db.example.com")]
    [DataRow(@"(localdb)\MSSQLLocalDB", ".")]
    public void DifferentServers_AreNot(string a, string b) =>
        Assert.IsFalse(SqlServerAddress.Parse(a).SameServerAs(b), $"{a} / {b}");
}
