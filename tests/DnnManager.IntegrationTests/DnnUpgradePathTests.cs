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
}
