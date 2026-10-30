using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;

namespace DnnManager.IntegrationTests;

/// <summary>DNN's suggested upgrade path: the chain of steps from a site's version to the one asked for.</summary>
[TestClass]
public sealed class DnnUpgradePathTests
{
    private static string Chain(string from, string to) =>
        string.Join(" | ", DnnUpgradePath.Chain(Version.Parse(from), Version.Parse(to)).Select(s => s.ToString()));

    [TestMethod]
    public void The_path_is_DNNs_own_in_order()
    {
        Assert.AreEqual("02.00.04 02.01.02 03.01.01 03.02.02 04.03.07 04.04.01 04.06.02 04.09.05 05.04.04 05.06.08 06.02.08 " +
                        "07.04.02 08.00.04 09.01.01 09.03.02 09.13.09 10.02.05 10.03.03",
            string.Join(" ", DnnUpgradePath.Versions.Select(DnnUpgradeStep.Name)));
    }

    [TestMethod]
    public void A_listed_version_goes_through_every_listed_one_after_it()
    {
        Assert.AreEqual("09.01.01 → 09.03.02 | 09.03.02 → 09.13.09 | 09.13.09 → 10.02.05 | 10.02.05 → 10.03.03", Chain("9.1.1", "10.3.3"));
        Assert.AreEqual("10.02.05 → 10.03.03", Chain("10.2.5", "10.3.3"));
    }

    [TestMethod]
    public void A_version_between_two_listed_ones_goes_to_the_next_listed_one_first()
    {
        Assert.AreEqual("09.05.00 → 09.13.09 | 09.13.09 → 10.02.05 | 10.02.05 → 10.03.03", Chain("9.5.0", "10.3.3"));
        // Never straight to DNN 10: through 9.13.9 even from a later 9.13.
        Assert.AreEqual("09.13.04 → 09.13.09 | 09.13.09 → 10.02.05", Chain("9.13.4", "10.2.5"));
        Assert.AreEqual("10.00.01 → 10.02.05 | 10.02.05 → 10.03.03", Chain("10.0.1", "10.3.3"));
    }

    [TestMethod]
    public void A_target_between_listed_versions_ends_the_chain_and_carries_the_steps_knowledge()
    {
        var chain = DnnUpgradePath.Chain(new Version(9, 3, 2), new Version(10, 1, 0));
        Assert.AreEqual("09.03.02 → 09.13.09 | 09.13.09 → 10.01.00", string.Join(" | ", chain));
        // 10.1.0 is a DNN 10: what DNN 10 needs applies.
        Assert.IsTrue(chain[1].Requirements.Any(r => r.Kind == DnnRequirementKind.SqlServer && r.Minimum == 14));
        Assert.IsTrue(chain[1].RemovesTelerik);
        Assert.IsTrue(chain[1].Warnings[0].Contains("between its listed versions"));
    }

    [TestMethod]
    public void A_target_past_the_newest_listed_version_says_so()
    {
        var chain = DnnUpgradePath.Chain(new Version(10, 2, 5), new Version(10, 4, 0));
        Assert.AreEqual("10.02.05 → 10.03.03 | 10.03.03 → 10.04.00", string.Join(" | ", chain));
        Assert.IsTrue(chain[1].Warnings[0].Contains("newer than the path's newest listed version"));
        // From a 10.2+ site on, DNN's own local upgrade.
        Assert.AreEqual(DnnUpgradeMethod.LocalUpgrade, chain[1].Method);
        Assert.AreEqual(DnnUpgradeMethod.UpgradePackage, DnnUpgradePath.Chain(new Version(9, 13, 9), new Version(10, 2, 5)).Single().Method);
    }

    [TestMethod]
    public void Steps_to_versions_GitHub_has_no_packages_of_are_manual()
    {
        var chain = DnnUpgradePath.Chain(new Version(5, 6, 8), new Version(8, 0, 4));
        Assert.AreEqual("05.06.08 → 06.02.08 | 06.02.08 → 07.04.02 | 07.04.02 → 08.00.04", string.Join(" | ", chain));
        CollectionAssert.AreEqual(new[] { DnnUpgradeMethod.Manual, DnnUpgradeMethod.UpgradePackage, DnnUpgradeMethod.UpgradePackage },
            chain.Select(s => s.Method).ToArray());
        Assert.AreEqual(DnnUpgradeMethod.Manual, DnnUpgradePath.Chain(new Version(1, 0, 0), new Version(2, 0, 4)).Single().Method);
    }

    [TestMethod]
    public void Nothing_to_do_when_the_target_isnt_newer()
    {
        Assert.AreEqual(0, DnnUpgradePath.Chain(new Version(10, 3, 3), new Version(10, 3, 3)).Count);
        Assert.AreEqual(0, DnnUpgradePath.Chain(new Version(10, 3, 3, 0), new Version(10, 2, 5)).Count);
    }

    [TestMethod]
    public void DNN_10_needs_NET_48_and_SQL_Server_2017_and_94_needs_472()
    {
        var toTen = DnnUpgradeKnowledge.Steps.Single(s => s.To == new Version(10, 2, 5));
        CollectionAssert.AreEquivalent(new[] { ".NET Framework 4.8", "SQL Server 2017" }, toTen.Requirements.Select(r => r.Text).ToArray());
        var toNine = DnnUpgradeKnowledge.Steps.Single(s => s.To == new Version(9, 13, 9));
        Assert.AreEqual(461808, toNine.Requirements.Single(r => r.Kind == DnnRequirementKind.NetFramework).Minimum);
    }

    // ─── Versions added in Settings ───────────────────────────────────────

    private static DnnUpgradeStopSetting Stop(string version, int netFramework = 0, int sqlServer = 0, string note = "") =>
        new() { Version = version, NetFrameworkRelease = netFramework, SqlServerMajor = sqlServer, Note = note };

    private static IReadOnlyList<DnnUpgradeStep> ChainWith(string from, string to, params DnnUpgradeStopSetting[] added) =>
        DnnUpgradePath.Chain(Version.Parse(from), Version.Parse(to), added);

    [TestMethod]
    public void An_added_version_is_a_stop_on_the_way_like_a_listed_one()
    {
        var chain = ChainWith("10.2.5", "10.5.0", Stop("10.4.2"));
        Assert.AreEqual("10.02.05 → 10.03.03 | 10.03.03 → 10.04.02 | 10.04.02 → 10.05.00", string.Join(" | ", chain));
        // The listed step is the path's own, with what was tested on it.
        Assert.IsTrue(chain[0].Tested.Count > 0);
        // From a 10.2+ site on, DNN's local upgrade - and what is known about the newest step goes on.
        Assert.AreEqual(DnnUpgradeMethod.LocalUpgrade, chain[1].Method);
        Assert.IsTrue(chain[1].Requirements.Any(r => r.Kind == DnnRequirementKind.SqlServer && r.Minimum == 14));
        // Never presented as tested, and the user is told it's their own.
        Assert.AreEqual(0, chain[1].Tested.Count);
        StringAssert.Contains(chain[1].Warnings[0], "You added DNN 10.04.02");
        // Past the newest stop the note names that one, not the built-in end of the path.
        StringAssert.Contains(chain[2].Warnings[0], "newest listed version (10.04.02)");
    }

    [TestMethod]
    public void An_added_version_between_two_listed_ones_is_a_stop_too()
    {
        var chain = ChainWith("10.2.5", "10.3.3", Stop("10.2.8"));
        Assert.AreEqual("10.02.05 → 10.02.08 | 10.02.08 → 10.03.03", string.Join(" | ", chain));
        Assert.IsFalse(chain[1].Warnings.Any(w => w.Contains("between its listed versions")), string.Join(" ", chain[1].Warnings));
    }

    [TestMethod]
    public void Added_versions_never_take_a_listed_stop_away()
    {
        // 9.13.9 and 10.2.5 stay, whatever is added around them.
        Assert.AreEqual("09.03.02 → 09.13.09 | 09.13.09 → 10.02.05 | 10.02.05 → 10.03.03 | 10.03.03 → 10.04.02 | 10.04.02 → 11.00.00",
            string.Join(" | ", ChainWith("9.3.2", "11.0.0", Stop("10.4.2"))));
    }

    [TestMethod]
    public void A_version_outside_the_upgrade_changes_nothing()
    {
        var chain = ChainWith("9.13.9", "10.3.3", Stop("10.4.2", sqlServer: 16, note: "Later."));
        Assert.AreEqual("09.13.09 → 10.02.05 | 10.02.05 → 10.03.03", string.Join(" | ", chain));
        Assert.IsTrue(chain.SelectMany(s => s.Requirements).All(r => r.Kind != DnnRequirementKind.SqlServer || r.Minimum == 14));
        Assert.IsFalse(chain.SelectMany(s => s.Warnings).Contains("Later."));
    }

    [TestMethod]
    public void A_minimum_holds_from_its_version_on_and_replaces_a_lower_one()
    {
        var chain = ChainWith("10.3.3", "11.2.0", Stop("10.4.2"), Stop("11.0.0", 533320, 15), Stop("11.1.0"));
        Assert.AreEqual("10.03.03 → 10.04.02 | 10.04.02 → 11.00.00 | 11.00.00 → 11.01.00 | 11.01.00 → 11.02.00", string.Join(" | ", chain));
        Assert.IsTrue(chain[0].Requirements.Any(r => r is { Kind: DnnRequirementKind.SqlServer, Minimum: 14 }), "Before the version that raised it.");
        foreach (var step in chain.Skip(1))
        {
            // One requirement of a kind - not 2017 and 2019 both.
            Assert.AreEqual("SQL Server 2019", step.Requirements.Single(r => r.Kind == DnnRequirementKind.SqlServer).Text, step.ToString());
            Assert.AreEqual(".NET Framework 4.8.1", step.Requirements.Single(r => r.Kind == DnnRequirementKind.NetFramework).Text, step.ToString());
        }
    }

    [TestMethod]
    public void A_minimum_below_what_is_known_changes_nothing()
    {
        // SQL Server 2016, .NET 4.5 - DNN 10 needs more, and that stays.
        var step = ChainWith("10.3.3", "10.4.2", Stop("10.4.2", 378389, 13)).Single();
        CollectionAssert.AreEquivalent(new[] { ".NET Framework 4.8", "SQL Server 2017" }, step.Requirements.Select(r => r.Text).ToArray());
    }

    [TestMethod]
    public void A_version_DNN_Manager_lists_itself_keeps_what_is_known_and_gets_the_additions()
    {
        var step = ChainWith("10.2.5", "10.3.3", Stop("10.3.3", sqlServer: 15, note: "Seen on our sites.")).Single();
        Assert.IsTrue(step.Tested.Count > 0);
        Assert.IsTrue(step.Warnings.Contains("Seen on our sites."));
        Assert.IsFalse(step.Warnings.Any(w => w.StartsWith("You added", StringComparison.Ordinal)), "A listed step isn't the user's own.");
        Assert.AreEqual("SQL Server 2019", step.Requirements.Single(r => r.Kind == DnnRequirementKind.SqlServer).Text);
    }

    [TestMethod]
    public void The_note_goes_with_the_step_to_its_version()
    {
        var chain = ChainWith("10.3.3", "10.5.0", Stop("10.4.2", note: "Read the release notes."));
        Assert.IsTrue(chain[0].Warnings.Contains("Read the release notes."));
        Assert.IsFalse(chain[1].Warnings.Contains("Read the release notes."));
    }

    [TestMethod]
    public void A_site_on_an_added_version_starts_from_it()
    {
        var chain = ChainWith("10.4.2", "10.5.0", Stop("10.4.2", sqlServer: 15));
        Assert.AreEqual("10.04.02 → 10.05.00", chain.Single().ToString());
        Assert.AreEqual("SQL Server 2019", chain.Single().Requirements.Single(r => r.Kind == DnnRequirementKind.SqlServer).Text);
    }

    [TestMethod]
    [DataRow("10.4.2", 10, 4, 2)]
    [DataRow(" 10.04.02 ", 10, 4, 2)]
    [DataRow("9.13.9", 9, 13, 9)]
    public void A_version_can_be_written_as_DNN_writes_it(string text, int major, int minor, int build) =>
        Assert.AreEqual(new Version(major, minor, build), SettingRules.UpgradeStopVersion(text));

    [TestMethod]
    [DataRow("")]
    [DataRow("10")]
    [DataRow("10.4")]
    [DataRow("10.4.2.1")]
    [DataRow("v10.4.2")]
    [DataRow("10.4.x")]
    [DataRow("10.4.-2")]
    [DataRow("10.4.2; calc")]
    [DataRow("9999.0.0")]
    public void Anything_else_isnt_a_version(string text)
    {
        Assert.IsNull(SettingRules.UpgradeStopVersion(text));
        Assert.IsNotNull(SettingRules.UpgradeStopsProblem([Stop(text)]));
    }

    [TestMethod]
    public void What_can_be_added_has_rules()
    {
        Assert.IsNull(SettingRules.UpgradeStopsProblem([]));
        Assert.IsNull(SettingRules.UpgradeStopsProblem([Stop("10.4.2", 533320, 18, "A note."), Stop("11.0.0")]));
        // A later DNN Manager may list it - the saved stop has to stay allowed then.
        Assert.IsNull(SettingRules.UpgradeStopsProblem([Stop("10.3.3")]));
        StringAssert.Contains(SettingRules.UpgradeStopsProblem([Stop("7.0.0")]), "older than 7.4.2");
        StringAssert.Contains(SettingRules.UpgradeStopsProblem([Stop("10.4.2"), Stop("10.04.02")]), "twice");
        Assert.IsNotNull(SettingRules.UpgradeStopsProblem([Stop("10.4.2", sqlServer: -1)]));
        Assert.IsNotNull(SettingRules.UpgradeStopsProblem([Stop("10.4.2", note: new string('x', SettingRules.MaxUpgradeStopNote + 1))]));
        Assert.IsNotNull(SettingRules.UpgradeStopsProblem([Stop("10.4.2", note: "two\nlines")]));
    }
}
