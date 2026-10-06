using System.IO.Pipes;
using System.Security.Claims;
using System.Security.Principal;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Broker;
using DnnManager.Infrastructure.Updates;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The DNN Manager Broker experiment (.docs/privileged-broker.md): who may use it, what it does for them, the messages on
/// its pipe - and, where the service is installed, the real thing.
/// </summary>
[TestClass]
public sealed class BrokerTests
{
    private const string Administrators = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const string Network = "S-1-5-2";

    // ─── Who may use it ───────────────────────────────────────────────────

    [TestMethod]
    public void An_elevated_administrator_may_use_the_broker() =>
        Assert.IsNull(BrokerServer.Refusal([Users, Administrators], []));

    [TestMethod]
    public void An_administrator_without_its_rights_may_use_the_broker() =>
        // UAC's filtered token: the Administrators group is there for deny only.
        Assert.IsNull(BrokerServer.Refusal([Users], [Administrators]));

    [TestMethod]
    public void A_user_who_is_no_administrator_is_refused() =>
        Assert.IsNotNull(BrokerServer.Refusal([Users], []));

    [TestMethod]
    public void A_caller_over_the_network_is_refused_even_as_administrator() =>
        Assert.IsNotNull(BrokerServer.Refusal([Users, Administrators, Network], []));

    [TestMethod]
    public void The_token_of_this_test_run_is_read_as_the_broker_reads_its_callers()
    {
        // What the service relies on: an unelevated administrator's token lists Administrators as a deny-only claim.
        using var me = WindowsIdentity.GetCurrent();
        var denyOnly = me.Claims.Where(c => c.Type == ClaimTypes.DenyOnlySid).Select(c => c.Value).ToList();
        var groups = me.Groups?.Select(g => g.Value).ToList() ?? [];
        if (!denyOnly.Contains(Administrators) && !groups.Contains(Administrators))
            Assert.Inconclusive("This test run's account isn't an administrator.");
        Assert.IsNull(BrokerServer.Refusal(groups, denyOnly));
    }

    [TestMethod]
    public void Signed_in_users_can_use_the_pipe_but_not_answer_on_it()
    {
        // The pipe's rule for them (ReadWrite) must not let them create an instance of their own.
        Assert.IsFalse(PipeAccessRights.ReadWrite.HasFlag(PipeAccessRights.CreateNewInstance));
    }

    // ─── What it does ─────────────────────────────────────────────────────

    [TestMethod]
    public void It_starts_stops_and_restarts_the_site_asked_for()
    {
        var iis = new UntouchedIis();
        var server = new BrokerServer(iis, NullLogger.Instance);

        Assert.IsTrue(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.StartSite, "alpha")).Success);
        Assert.IsTrue(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.StopSite, "alpha")).Success);
        Assert.IsTrue(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.StopSiteAndWait, "alpha", 5)).Success);
        Assert.IsTrue(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.RestartSite, "alpha")).Success);

        CollectionAssert.AreEqual(new[] { "StartSite alpha", "StopSite alpha", "StopSite alpha", "RestartSite alpha" }, iis.Changes);
    }

    [TestMethod]
    public void It_refuses_what_it_doesnt_know_and_changes_nothing()
    {
        var iis = new UntouchedIis();
        var server = new BrokerServer(iis, NullLogger.Instance);

        Assert.IsFalse(server.Handle(BrokerRequest.For("site.remove", "alpha")).Success);
        Assert.IsFalse(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.StartSite)).Success, "no site");
        Assert.IsFalse(server.Handle(BrokerRequest.For(BrokerProtocol.Operations.StartSite, "al\npha")).Success, "control character");
        Assert.IsFalse(server.Handle(new BrokerRequest(BrokerProtocol.Version + 1, BrokerProtocol.Operations.StartSite, "alpha")).Success,
            "another version");
        Assert.AreEqual(0, iis.Changes.Count);
    }

    [TestMethod]
    public void Every_site_s_runtime_arrives_as_it_was_read()
    {
        var site = new IisSiteRuntime(3, "Started", "alpha", "Started", [1234],
            [new IisBinding("http", "*", 80, "alpha.dnndev.me", false), new IisBinding("https", "*", 443, "", true)], @"C:\DNN\alpha")
        { IdleTimeout = TimeSpan.FromMinutes(20) };
        var sent = new Dictionary<string, IisSiteRuntime> { ["alpha"] = site };

        var arrived = BrokerResponse.Ok(sent).DataAs<Dictionary<string, IisSiteRuntime>>();

        Assert.IsTrue(site.SameAs(arrived?["alpha"]));
        CollectionAssert.AreEqual(new[] { 80, 443 }, arrived!["alpha"].Ports.ToArray());
    }

    // ─── The messages on the pipe ─────────────────────────────────────────

    [TestMethod]
    public async Task A_request_is_one_line_read_back_as_it_was_written()
    {
        using var stream = new MemoryStream();
        await BrokerProtocol.WriteAsync(stream, BrokerRequest.For(BrokerProtocol.Operations.StopSiteAndWait, "alpha", 30), CancellationToken.None);
        stream.Position = 0;

        var read = await BrokerProtocol.ReadAsync<BrokerRequest>(stream, BrokerProtocol.MaxRequestBytes, CancellationToken.None);

        Assert.AreEqual(BrokerRequest.For(BrokerProtocol.Operations.StopSiteAndWait, "alpha", 30), read);
    }

    [TestMethod]
    public async Task A_request_longer_than_allowed_is_not_read()
    {
        using var stream = new MemoryStream(new byte[BrokerProtocol.MaxRequestBytes + 10_000]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            BrokerProtocol.ReadAsync<BrokerRequest>(stream, BrokerProtocol.MaxRequestBytes, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_connection_closed_without_a_message_reads_as_none()
    {
        using var stream = new MemoryStream();
        Assert.IsNull(await BrokerProtocol.ReadAsync<BrokerRequest>(stream, BrokerProtocol.MaxRequestBytes, CancellationToken.None));
    }

    // ─── DNN Manager's side ───────────────────────────────────────────────

    [TestMethod]
    public void The_brokered_IIS_implements_every_member_itself()
    {
        // An interface member it left out would quietly run IIIsManager's default ("Not supported here.") instead of IIS.
        var map = typeof(BrokeredIisManager).GetInterfaceMap(typeof(IIisManager));
        var inherited = map.TargetMethods.Where(m => m.DeclaringType != typeof(BrokeredIisManager)).Select(m => m.Name).ToList();
        Assert.AreEqual(0, inherited.Count, "Not implemented: " + string.Join(", ", inherited));
    }

    [TestMethod]
    public void A_test_run_is_not_a_package() => Assert.IsFalse(PackageIdentity.IsPackaged);

    // ─── The real service, where it is installed ──────────────────────────

    [TestMethod, TestCategory("Integration")]
    public async Task The_installed_service_answers_and_reads_IIS()
    {
        if (!BrokerClient.IsServiceRunning)
            Assert.Inconclusive($"The {BrokerProtocol.DisplayName} isn't running - install it with DnnManager.exe --broker install.");

        var client = new BrokerClient();
        var ping = await client.SendAsync(BrokerRequest.For(BrokerProtocol.Operations.Ping), CancellationToken.None);
        Assert.IsTrue(ping.Success, ping.Error);
        Assert.AreEqual(BrokerProtocol.Version, ping.DataAs<int>());

        var states = await client.SendAsync(BrokerRequest.For(BrokerProtocol.Operations.SiteStates), CancellationToken.None);
        Assert.IsTrue(states.Success, states.Error);
        Assert.IsNotNull(states.DataAs<Dictionary<string, string>>());
    }
}
