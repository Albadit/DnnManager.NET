using DnnManager.Application.Abstractions;
using DnnManager.Application.Upgrades;

namespace DnnManager.IntegrationTests;

/// <summary>The pre-upgrade analyser's rules: what blocks an upgrade, what only warns, and what couldn't be checked.</summary>
[TestClass]
public sealed class DnnUpgradeAnalyserTests
{
    /// <summary>A healthy DNN 9.13.9 site on SQL Server 2022 and .NET 4.8.1 - each test changes what it is about.</summary>
    private static DnnSiteFacts Site(string version = "9.13.9") => new()
    {
        SiteName = "shop", Directory = @"C:\DNN\shop",
        FilesVersion = version, DatabaseVersion = version,
        NetFrameworkRelease = 533320, AppPoolClr = "v4.0", AppPoolPipeline = "Integrated", HasHostBinding = true,
        SiteBytes = 300L << 20, BackupFreeBytes = 50L << 30,
        DatabaseKind = DatabaseKind.Container, DatabaseName = "shop", SqlServerVersion = "16.0.4135.4", SqlServerEdition = "Developer Edition",
        DatabaseState = "ONLINE", Counts = new DnnContentCounts(1, 3, 4, 5, 30, 40, 200, 20, 90), HasMachineKey = true,
        Extensions = [new DnnExtension("DotNetNuke.HTML", "HTML", "Module", "9.13.9", "DNN", true)]
    };

    private static DnnUpgradePlan Plan(DnnSiteFacts facts, string target = "10.3.3") => DnnUpgradeAnalyser.Plan(facts, Version.Parse(target));

    private static IEnumerable<UpgradeFinding> Blocking(DnnUpgradePlan plan) => plan.All.Where(f => f.Severity == UpgradeFindingSeverity.Blocking);

    [TestMethod]
    public void A_healthy_site_can_start_and_the_plan_has_every_step()
    {
        var plan = Plan(Site());
        Assert.IsTrue(plan.CanStart, string.Join(Environment.NewLine, Blocking(plan)));
        Assert.AreEqual("09.13.09 → 10.02.05 | 10.02.05 → 10.03.03", string.Join(" | ", plan.Steps.Select(s => s.Step)));
        // The DNN 10 step: its requirements checked and met.
        var ten = plan.Steps[0].Checks;
        Assert.IsTrue(ten.Any(c => c.Severity == UpgradeFindingSeverity.Compatible && c.Title.StartsWith("SQL Server 2017")));
        Assert.IsTrue(ten.Any(c => c.Severity == UpgradeFindingSeverity.Compatible && c.Title.StartsWith(".NET Framework 4.8")));
        StringAssert.Contains(plan.Describe(), "Required path:");
    }

    [TestMethod]
    public void An_old_SQL_Server_blocks_the_step_to_DNN_10()
    {
        var plan = Plan(Site() with { SqlServerVersion = "13.0.5026.0" });
        Assert.IsFalse(plan.CanStart);
        // Both DNN 10 steps need it - the first is where it stops.
        CollectionAssert.AreEqual(new[] { "DNN 10.02.05 needs SQL Server 2017", "DNN 10.03.03 needs SQL Server 2017" }, Blocking(plan).Select(b => b.Title).ToArray());
        StringAssert.Contains(Blocking(plan).First().Detail, "SQL Server 2016");
    }

    [TestMethod]
    public void An_old_NET_Framework_blocks_the_steps_that_need_a_newer_one()
    {
        // 4.7.2 is enough for 9.13.9 but not for DNN 10.
        var plan = Plan(Site("9.3.2") with { NetFrameworkRelease = 461808 });
        Assert.AreEqual("DNN 10.02.05 needs .NET Framework 4.8", Blocking(plan).First().Title);
        Assert.IsTrue(Blocking(plan).All(b => b.Title.EndsWith(".NET Framework 4.8")));
        Assert.IsTrue(plan.Steps[0].Checks.Any(c => c.Severity == UpgradeFindingSeverity.Compatible && c.Title.StartsWith(".NET Framework 4.7.2")));
    }

    [TestMethod]
    public void Telerik_dependent_extensions_block_the_step_to_DNN_10_and_only_that_one()
    {
        var facts = Site("9.3.2") with
        {
            TelerikInBin = true,
            Assemblies = [new DnnAssembly("Acme.Gallery.dll", "Acme.Gallery", new Version(9, 1, 0), UsesTelerik: true, Registered: true)]
        };
        var plan = Plan(facts);
        var block = Blocking(plan).Single();
        Assert.AreEqual("Telerik", block.Area);
        StringAssert.Contains(block.Title, "Acme.Gallery.dll");
        Assert.AreEqual(1, plan.Steps.Count(s => s.Checks.Any(c => c.Severity == UpgradeFindingSeverity.Blocking)), "Only DNN 10's step is blocked.");
        // On the way, 9.8 recommends removing it.
        Assert.IsTrue(plan.Steps[0].Checks.Any(c => c.Area == "Telerik" && c.Severity == UpgradeFindingSeverity.Warning));
    }

    [TestMethod]
    public void Telerik_only_in_DNNs_own_files_is_fine()
    {
        var plan = Plan(Site() with { TelerikInBin = true });
        Assert.IsTrue(plan.CanStart);
        Assert.IsTrue(plan.Steps[0].Checks.Any(c => c.Area == "Telerik" && c.Severity == UpgradeFindingSeverity.Compatible));
    }

    [TestMethod]
    public void Extensions_built_for_DNN_7_warn_on_the_step_past_92()
    {
        var plan = Plan(Site("9.1.1") with { Assemblies = [new DnnAssembly("Old.Module.dll", "Old.Module", new Version(7, 0, 0), false, true)] });
        Assert.IsTrue(plan.CanStart);
        var warning = plan.Steps.Single(s => s.Step.To == new Version(9, 3, 2)).Checks.Single(c => c.Area == "Extensions");
        Assert.AreEqual(UpgradeFindingSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Detail, "DNN 9.2 removed");
    }

    [TestMethod]
    public void Files_and_database_out_of_step_block()
    {
        var plan = Plan(Site() with { DatabaseVersion = "9.13.4" });
        StringAssert.Contains(Blocking(plan).Single().Title, "the database at 9.13.4");
    }

    [TestMethod]
    public void A_LocalDB_file_or_an_unreadable_database_blocks_and_unknowns_are_said()
    {
        Assert.AreEqual("The database is a LocalDB file", Blocking(Plan(Site() with { DatabaseKind = DatabaseKind.LocalDbFile })).Single().Title);
        Assert.AreEqual("The database can't be read",
            Blocking(Plan(Site() with { DatabaseProblem = "Login failed", SqlServerVersion = null, DatabaseVersion = null })).First().Title);
        var unknown = Plan(Site() with { SqlServerVersion = null });
        Assert.IsTrue(unknown.CanStart, "What couldn't be checked doesn't block…");
        Assert.IsTrue(unknown.All.Any(f => f.Severity == UpgradeFindingSeverity.Unknown && f.Title.StartsWith("SQL Server 2017")), "…but it is said.");
    }

    [TestMethod]
    public void Manual_steps_too_little_space_and_a_wrong_app_pool_block()
    {
        Assert.AreEqual("DNN Manager can't carry out this step", Blocking(Plan(Site("6.1.0"), "7.4.2")).Single().Title);
        Assert.IsTrue(Blocking(Plan(Site() with { BackupFreeBytes = 100L << 20 })).Any(f => f.Area == "Backups"));
        Assert.IsTrue(Blocking(Plan(Site() with { AppPoolClr = "v2.0" })).Any(f => f.Area == "IIS"));
        Assert.IsTrue(Blocking(Plan(Site() with { HasHostBinding = false })).Any(f => f.Area == "IIS"));
    }

    [TestMethod]
    public void Third_party_extensions_and_custom_settings_warn()
    {
        var plan = Plan(Site() with
        {
            Extensions = [new DnnExtension("Acme.Blog", "Acme Blog", "Module", "3.1.0", "Acme", false)],
            CustomAppSettings = ["Acme.ApiKey"],
            Assemblies = [new DnnAssembly("Hand.Copied.dll", "Hand.Copied", null, false, Registered: false)],
            HasMachineKey = false
        });
        Assert.IsTrue(plan.CanStart);
        foreach (var area in new[] { "Extensions", "web.config", "Files" })
            Assert.IsTrue(plan.Site.Any(f => f.Area == area && f.Severity == UpgradeFindingSeverity.Warning), area);
    }
}
