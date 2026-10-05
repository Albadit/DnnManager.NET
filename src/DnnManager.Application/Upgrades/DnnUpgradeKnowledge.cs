namespace DnnManager.Application.Upgrades;

/// <summary>
/// What is known about each step of DNN's suggested upgrade path - the one place to maintain it. Each step: its
/// requirements (checked before the upgrade starts), warnings, breaking changes and known issues (shown in the plan),
/// and how DNN Manager's tests carried it out on real sites. Sources: DNN's suggested upgrade path and requirements
/// pages, its release notes and issues, and its source code (links in docs/dnn-upgrades.md). When DNN adds a version
/// to its path, add a step at the end.
/// </summary>
public static class DnnUpgradeKnowledge
{
    private const string PathDoc = DnnUpgradePath.Source;
    private const string Requirements10 = "https://docs.dnncommunity.org/content/getting-started/setup/requirements/index.html";

    /// <summary>Seen on every DNN 10 step, on IIS above all (DNN's InstallBlocker: File.Create without closing the file).</summary>
    private const string LockLeft =
        "Tested here: DNN opens its installBlocker.lock and doesn't close it, so at the end of its upgrade it tries for a minute to delete " +
        "it and fails (an IOException in its log). Until the site's worker process ends, the lock stays and DNN takes every visit for an " +
        "upgrade in progress. DNN Manager stops the site, waits for the worker process to end and deletes the lock before the checks.";

    private static DnnRequirement NetFramework(int release, string text, string source = PathDoc) => new(DnnRequirementKind.NetFramework, release, text, source);
    private static DnnRequirement SqlServer(int major, string text, string source = PathDoc) => new(DnnRequirementKind.SqlServer, major, text, source);

    private static DnnUpgradeStep Step(string from, string to) => new()
    {
        From = Version.Parse(from), To = Version.Parse(to), Method = DnnUpgradePath.MethodFor(Version.Parse(from))
    };

    /// <summary>A step DNN Manager can't carry out: GitHub has no packages of DNN 6 and older.</summary>
    private static DnnUpgradeStep Manual(string from, string to, params string[] warnings) => Step(from, to) with
    {
        Method = DnnUpgradeMethod.Manual,
        Warnings = [.. warnings, "GitHub publishes no DNN packages older than 7.4.2: this step is done by hand, with the packages from DNN's archive."]
    };

    public static IReadOnlyList<DnnUpgradeStep> Steps { get; } =
    [
        Manual("2.0.4", "2.1.2"),
        Manual("2.1.2", "3.1.1"),
        Manual("3.1.1", "3.2.2"),
        Manual("3.2.2", "4.3.7", "DNN 4.3 moved to ASP.NET 2.0."),
        Manual("4.3.7", "4.4.1"),
        Manual("4.4.1", "4.6.2", "DNN 4.6.2 brought xmlmerge, which merges web.config changes during upgrades - before it, machineKey and connection strings were merged by hand."),
        Manual("4.6.2", "4.9.5"),
        Manual("4.9.5", "5.4.4", "DNN 5.2 and newer need SQL Server 2005 and ASP.NET 3.5 SP1.",
            "With the XML module installed, DNN 5.3 fails with \"Type 'Web.HttpResponse' is not defined\" - upgrade the XML module first."),
        Manual("5.4.4", "5.6.8"),
        Manual("5.6.8", "6.2.8"),
        Step("6.2.8", "7.4.2") with
        {
            Requirements = [NetFramework(0, ".NET Framework 4")],
            Warnings = ["DNN 7 changed its prerequisites: it runs on ASP.NET 4 (CLR v4.0) - the app pool must be set to it."]
        },
        Step("7.4.2", "8.0.4") with
        {
            Requirements = [NetFramework(378675, ".NET Framework 4.5.1", "http://archive.dnnsoftware.com/docs/85/designers/requirements.html"),
                            SqlServer(10, "SQL Server 2008 R2", "http://archive.dnnsoftware.com/docs/85/designers/requirements.html")],
            KnownIssues =
            [
                "Tested here: DNN 8 removes ten of its own Admin and Host pages (SQL, Lists, Dashboard, Pages, Languages, Recycle Bin, Log Viewer…) and " +
                "eleven of its extensions (Banners, Newsletters, Site Log, Vendors, Skin Designer…) with their modules - expected, not lost content. " +
                "DNN 8 has no Persona Bar yet: its control bar stays."
            ],
            Tested = ["New project at 7.4.2, then 8.0.4 and on through every step to 10.3.3: PASS"]
        },
        Step("8.0.4", "9.1.1") with
        {
            Requirements = [NetFramework(378675, ".NET Framework 4.5.1"), SqlServer(10, "SQL Server 2008")],
            KnownIssues =
            [
                "Tested here: DNN 9 replaces its Admin and Host pages with the Persona Bar - about 50 pages and modules go, with 27 extensions - and " +
                "removes the Console modules (\"Navigation\") its site template put on the Activity Feed pages. Expected, not lost content."
            ],
            Tested = ["New project at 8.0.4, then every step to 10.3.3: PASS", "7.4.2 → 8.0.4 → 9.1.1 → … → 10.3.3: PASS"]
        },
        Step("9.1.1", "9.3.2") with
        {
            Requirements = [SqlServer(10, "SQL Server 2008")],
            BreakingChanges =
            [
                "DNN 9.2 removed about 500 APIs deprecated in earlier versions: extensions still using them stop working.",
                "DNN 9.2 renamed SharpZipLib's assembly; extensions built against the old name need a new version."
            ],
            Warnings = ["Check that every third-party extension has a version for DNN 9.2 or newer before upgrading."],
            KnownIssues = ["DNN issue #2631: a site answered 503 after Install.aspx?mode=upgrade (9.2.2 → 9.3) - DNN Manager restarts the site after every upgrade."],
            Tested = ["New project at 9.1.1, then every step to 10.3.3: PASS"]
        },
        Step("9.3.2", "9.13.9") with
        {
            Requirements = [NetFramework(461808, ".NET Framework 4.7.2"), SqlServer(10, "SQL Server 2008 R2")],
            Warnings =
            [
                "DNN 9.4 needs ASP.NET 4.7.2 - web.config's targetFramework is raised to 4.7.2.",
                "From DNN 9.8 on, removing Telerik is optional but highly recommended - DNN 10 removes it. From 9.11 on, DNN's " +
                "TelerikRemoval extension removes it during the upgrade unless told to keep it (host setting telerikUninstallOption = N)."
            ],
            KnownIssues =
            [
                "DNN issues #5278 / #5336: 9.11's Telerik removal stops on an old Messaging module (DNN 6 or older) it counts as using Telerik.",
                "DNN issue #5862: the Resource Manager didn't load after upgrading to 9.13 on some sites.",
                "Tested here: through DNN's unattended upgrade Telerik stays at 9.13.9 (Telerik.Web.UI.dll and DotNetNuke.Web.Deprecated.dll remain) - DNN 10 removes it."
            ],
            Tested = ["9.3.2 → 9.13.9 with users, a role, pages, an HTML module, a child portal and a custom appSetting: PASS"]
        },
        Step("9.13.9", "10.2.5") with
        {
            Requirements = [NetFramework(528040, ".NET Framework 4.8", Requirements10), SqlServer(14, "SQL Server 2017", Requirements10)],
            RemovesTelerik = true,
            BreakingChanges =
            [
                "DNN 10 removes the Telerik assemblies DNN shipped (forced): extensions built on them stop working.",
                "DNN 10 removes the APIs deprecated in DNN 8 and earlier, and WebSlices; Prompt commands and connectors need dependency injection.",
                "DNN 10's HTML module uses the new workflow: publish or discard unapproved HTML content before upgrading.",
                "DNN 10 replaces the Digital Assets Manager with the Resource Manager."
            ],
            Warnings =
            [
                "Check that every third-party extension has a version for DNN 10 before upgrading.",
                "DNN 9.13.10 is a security hotfix and doesn't need to be a stop on the way."
            ],
            KnownIssues =
            [
                "DNN issue #6448: the CodeDom 3.6 → 4.1 change can fail with compile error -5324627661 - uninstall CodeDom 3.6 first, or remove web.config's compilers section.",
                "DNN issue #6993: 9.13.10 → 10.2.2 failed with \"Could not load System.Memory\" - fixed in 10.2.3; 10.2.5 is the step's target for that reason.",
                LockLeft,
                "Tested here: DNN 10 removes its TelerikRemoval extension, and the //Host//TelerikRemoval page and its module with it - expected, not lost content."
            ],
            Tested =
            [
                "9.3.2 → 9.13.9 → 10.2.5 → 10.3.3 with users, a role, pages, an HTML module, a child portal and a custom appSetting: PASS",
                "New project at 9.13.9, then 10.2.5 and 10.3.3: PASS - DNN's lock removed by the restart each time, the site stopped as IIS stops it"
            ]
        },
        Step("10.2.5", "10.3.3") with
        {
            Requirements = [NetFramework(528040, ".NET Framework 4.8", Requirements10), SqlServer(14, "SQL Server 2017", Requirements10)],
            Warnings =
            [
                "From DNN 10.2 on, DNN upgrades from its install package (Settings → Servers → System Info → Upgrades); DNN's release " +
                "notes say not to unzip the upgrade package over it. DNN Manager puts the install package in as that upgrade does."
            ],
            KnownIssues =
            [
                "Tested here: the upgrade package unzipped over 10.2.5 leaves web.config's binding redirects behind the new assemblies " +
                "(\"Could not load file or assembly 'AngleSharp'\") and the site doesn't start - the reason DNN Manager uses DNN's own local upgrade from 10.2 on.",
                "DNN issue #7141: 10.2.3 can't upload the next version's package from its Persona Bar - it has to be put in App_Data\\Upgrade by hand. DNN Manager doesn't upload it.",
                "DNN issue #7069: sites upgraded to 10.2.0–10.2.3 miss <add key=\"AllowDnnUpgradeUpload\" value=\"true\" /> - the Upload Package button doesn't show (10.2.4 adds it).",
                "DNN issues #7158, #7305: DDRMenu crashed after 10.3.0 on some sites; new HTML modules stayed Draft after 10.2.3 → 10.3.2.",
                "Tested here: when one of DNN's database steps fails (a trigger refusing UpdateDatabaseVersionAndName), DNN abandons the upgrade without an " +
                "\"Error!\" and then fails on its own lock - the real error is only in DNN's log (Portals\\_default\\Logs), which DNN Manager reads and keeps.",
                LockLeft
            ],
            Tested =
            [
                "Upgrade package unzipped over 10.2.5: FAIL (AngleSharp binding redirect) - the reason for DNN's local upgrade",
                "DNN's local upgrade (install package): PASS, also as the last step of 9.3.2 → 10.3.3 with content",
                "New project at 10.2.5, then 10.3.3: PASS",
                "A failing database step, on a database imported from a .bacpac: the chain stops, the diagnosis names the script and its error, " +
                "the backup's database goes back in, and the site is back at 10.2.5 and works"
            ]
        }
    ];
}
