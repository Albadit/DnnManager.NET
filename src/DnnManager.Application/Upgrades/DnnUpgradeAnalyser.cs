using DnnManager.Application.Abstractions;

namespace DnnManager.Application.Upgrades;

/// <summary>How a finding of the pre-upgrade analyser weighs on the upgrade.</summary>
public enum UpgradeFindingSeverity
{
    /// <summary>Checked and fine.</summary>
    Compatible,

    /// <summary>Doesn't stop the upgrade, but needs a look - before or after it.</summary>
    Warning,

    /// <summary>The upgrade doesn't start while this is so.</summary>
    Blocking,

    /// <summary>Couldn't be checked - said, so nobody counts on it.</summary>
    Unknown
}

/// <summary>Something the analyser found: what about (<paramref name="Area"/>), what, why it matters, and what to do.</summary>
public sealed record UpgradeFinding(UpgradeFindingSeverity Severity, string Area, string Title, string? Detail = null, string? Fix = null)
{
    public override string ToString() => $"{Severity}: {Area} - {Title}{(Detail is null ? "" : $" ({Detail})")}";
}

/// <summary>A step of the plan with what was checked for it.</summary>
public sealed record PlannedStep(DnnUpgradeStep Step, IReadOnlyList<UpgradeFinding> Checks);

/// <summary>
/// The upgrade plan: the site's version, the target, the steps of DNN's path between them each with its checks, and
/// what was found about the site as a whole. It starts only when nothing is blocking (<see cref="CanStart"/>).
/// </summary>
public sealed record DnnUpgradePlan(Version Current, Version Target, IReadOnlyList<PlannedStep> Steps, IReadOnlyList<UpgradeFinding> Site)
{
    public IEnumerable<UpgradeFinding> All => Site.Concat(Steps.SelectMany(s => s.Checks));

    public bool CanStart => Steps.Count > 0 && All.All(f => f.Severity != UpgradeFindingSeverity.Blocking);

    public IReadOnlyList<UpgradeFinding> Blocking => All.Where(f => f.Severity == UpgradeFindingSeverity.Blocking).ToList();

    /// <summary>The plan as text - for the Output tab and each stage's backup folder.</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            $"Current: DNN {DnnUpgradeStep.Name(Current)}",
            $"Target:  DNN {DnnUpgradeStep.Name(Target)}",
            "",
            "Required path:"
        };
        lines.AddRange(Steps.Select((s, i) => $"  {i + 1}. {s.Step}"));
        lines.Add("");
        lines.Add("The site:");
        lines.AddRange(Site.Select(f => "  " + Line(f)));
        foreach (var (step, i) in Steps.Select((s, i) => (s, i)))
        {
            lines.Add("");
            lines.Add($"Step {i + 1} - {step.Step}");
            lines.AddRange(step.Checks.Select(f => "  " + Line(f)));
        }
        return string.Join(Environment.NewLine, lines);

        static string Line(UpgradeFinding f) =>
            $"[{f.Severity}] {f.Area}: {f.Title}" + (f.Detail is null ? "" : $" - {f.Detail}") + (f.Fix is null ? "" : $" → {f.Fix}");
    }
}

/// <summary>
/// The pre-upgrade analyser's rules: what was read about a site (<see cref="DnnSiteFacts"/>) against DNN's upgrade path
/// (<see cref="DnnUpgradePath"/>) - every step's requirements, what it removes, and what the site holds that it may break.
/// Each finding is Compatible, Warning, Blocking or Unknown; a blocking one keeps the upgrade from starting. Pure: the
/// facts are read elsewhere (<see cref="IDnnSiteInspector"/>).
/// </summary>
public static class DnnUpgradeAnalyser
{
    /// <summary>DNN 9.2 removed the APIs deprecated in DNN 7 and earlier.</summary>
    private static readonly Version RemovedApis = new(9, 2, 0);

    private static readonly Version Ten = new(10, 0, 0);

    public static DnnUpgradePlan Plan(DnnSiteFacts facts, Version target)
    {
        var site = new List<UpgradeFinding>();
        if (facts.FilesVersion is null || DnnInstallVersion(facts.FilesVersion) is not { } current)
        {
            site.Add(new(UpgradeFindingSeverity.Blocking, "DNN", "No DNN version found in the site's files", @"bin\DotNetNuke.dll is missing or can't be read."));
            return new DnnUpgradePlan(new Version(0, 0, 0), target, [], site);
        }
        var chain = DnnUpgradePath.Chain(current, target);
        if (chain.Count == 0)
            site.Add(new(UpgradeFindingSeverity.Blocking, "DNN", $"DNN {DnnUpgradeStep.Name(target)} isn't newer than the site's DNN {DnnUpgradeStep.Name(current)}"));

        SiteChecks(facts, current, site);
        var steps = chain.Select(step => new PlannedStep(step, StepChecks(facts, step))).ToList();
        return new DnnUpgradePlan(current, DnnUpgradePath.Normalize(target), steps, site);
    }

    // ─── The site as a whole ─────────────────────────────────────────────

    private static void SiteChecks(DnnSiteFacts f, Version current, List<UpgradeFinding> found)
    {
        // DNN's version in its files and in its database must agree: otherwise an earlier upgrade didn't finish.
        if (f.DatabaseVersion is null)
            found.Add(new(UpgradeFindingSeverity.Unknown, "DNN", "The database's DNN version couldn't be read", f.DatabaseProblem));
        else if (DnnInstallVersion(f.DatabaseVersion) is { } db && DnnUpgradePath.Normalize(db) != current)
            found.Add(new(UpgradeFindingSeverity.Blocking, "DNN", $"The files are at DNN {f.FilesVersion}, the database at {f.DatabaseVersion}",
                "An earlier upgrade didn't run or didn't finish.", "Undo it first: Restore backup (the project's right-click menu) puts back the backup made before it (\"Before upgrading DNN …\"), then upgrade again."));
        else
            found.Add(new(UpgradeFindingSeverity.Compatible, "DNN", $"DNN {f.FilesVersion} - files and database agree"));
        if (f.InstallBlocked)
            found.Add(new(UpgradeFindingSeverity.Warning, "DNN", "installBlocker.lock is there",
                "An installation or upgrade is running, or one was cut off. DNN Manager removes it before upgrading.",
                "Make sure nobody runs DNN's installer or upgrade wizard meanwhile."));

        // The database.
        if (f.DatabaseKind is null)
            found.Add(new(UpgradeFindingSeverity.Blocking, "Database", "The site's web.config names no database of its own"));
        else if (f.DatabaseKind == DatabaseKind.LocalDbFile)
            found.Add(new(UpgradeFindingSeverity.Blocking, "Database", "The database is a LocalDB file",
                "It can't be backed up as .bacpac, so there would be no way back.", "Move it to SQL Server first (Details → Database → Change connection…)."));
        else if (f.DatabaseProblem is not null)
            found.Add(new(UpgradeFindingSeverity.Blocking, "Database", "The database can't be read", f.DatabaseProblem,
                "Check that the SQL Server is running and the site's login may sign in."));
        else
        {
            found.Add(f.DatabaseState is null or "ONLINE"
                ? new(UpgradeFindingSeverity.Compatible, "Database", $"[{f.DatabaseName}] on {(f.SqlServerMajor is { } m ? DnnSiteFacts.SqlServerName(m) : "SQL Server")} {f.SqlServerVersion} ({f.SqlServerEdition})")
                : new(UpgradeFindingSeverity.Blocking, "Database", $"[{f.DatabaseName}] is {f.DatabaseState}", "Only an ONLINE database can be upgraded."));
            if (f.Counts is { } counts)
                found.Add(new(UpgradeFindingSeverity.Compatible, "Content", counts.ToString(),
                    counts.Portals > 1 ? "Several portals: each one's home page is checked after every step." : null));
        }

        // IIS and this PC.
        if (!f.HasHostBinding)
            found.Add(new(UpgradeFindingSeverity.Blocking, "IIS", "The site has no http binding with a host name",
                "DNN's upgrade is run through one.", "Add one (Details → IIS → Edit host names…)."));
        if (f.AppPoolClr is { } clr && !clr.Equals("v4.0", StringComparison.OrdinalIgnoreCase))
            found.Add(new(UpgradeFindingSeverity.Blocking, "IIS", $"The app pool runs .NET CLR {(clr.Length == 0 ? "none" : clr)}",
                "DNN 7 and newer run on CLR v4.0.", "Set the app pool's .NET CLR version to v4.0 (Details → IIS → Edit app pool…)."));
        else if (f.AppPoolClr is not null)
            found.Add(new(UpgradeFindingSeverity.Compatible, "IIS", $"App pool: .NET CLR {f.AppPoolClr}, {f.AppPoolPipeline} pipeline"));
        if (f.AppPoolPipeline is { } pipeline && pipeline.Equals("Classic", StringComparison.OrdinalIgnoreCase))
            found.Add(new(UpgradeFindingSeverity.Warning, "IIS", "The app pool uses the Classic pipeline", "DNN is made for the Integrated pipeline."));
        if (f.NetFrameworkRelease is null)
            found.Add(new(UpgradeFindingSeverity.Unknown, "This PC", ".NET Framework 4.x wasn't found"));
        if (f.BackupFreeBytes is { } free && free < 2 * f.SiteBytes + 500L * 1024 * 1024)
            found.Add(new(UpgradeFindingSeverity.Blocking, "Backups", $"Too little free space for the backups: {free / 1048576:N0} MB",
                $"Every step backs up the site ({f.SiteBytes / 1048576:N0} MB) and its database first.", "Free some space on the drive of Documents\\DnnManager."));

        // web.config.
        if (f.HasMachineKey == false)
            found.Add(new(UpgradeFindingSeverity.Warning, "web.config", "web.config has no machineKey",
                "DNN writes a new one during the upgrade - what was encrypted with the old keys can't be read any more."));
        if (f.CustomAppSettings.Count > 0)
            found.Add(new(UpgradeFindingSeverity.Warning, "web.config", $"{f.CustomAppSettings.Count} appSetting(s) DNN doesn't ship: {string.Join(", ", f.CustomAppSettings.Take(8))}",
                "web.config isn't replaced - they stay - but check them after the upgrade."));
        if (f.ConnectionStrings.Count > 1)
            found.Add(new(UpgradeFindingSeverity.Warning, "web.config", $"{f.ConnectionStrings.Count} connection strings: {string.Join(", ", f.ConnectionStrings)}",
                "Only SiteSqlServer's database is backed up and upgraded."));

        // Extensions.
        var theirs = f.Extensions.Where(e => !e.IsDnns).ToList();
        if (theirs.Count > 0)
            found.Add(new(UpgradeFindingSeverity.Warning, "Extensions", $"{theirs.Count} third-party extension(s)",
                string.Join(", ", theirs.Take(12).Select(e => $"{e.FriendlyName} {e.Version} ({e.Type}{(e.Owner is { Length: > 0 } o ? $", {o}" : "")})")) + (theirs.Count > 12 ? ", …" : ""),
                "Check that each has a version for the target DNN."));
        else if (f.Extensions.Count > 0)
            found.Add(new(UpgradeFindingSeverity.Compatible, "Extensions", $"{f.Extensions.Count} extensions, all DNN's own"));
        var unregistered = f.Assemblies.Where(a => !a.Registered).ToList();
        if (unregistered.Count > 0)
            found.Add(new(UpgradeFindingSeverity.Warning, "Files", $"{unregistered.Count} assembly(ies) in bin no extension installed: {string.Join(", ", unregistered.Take(10).Select(a => a.File))}",
                "Copied in by hand - the upgrade leaves them, but nothing checks they fit the new DNN."));
    }

    // ─── Each step ───────────────────────────────────────────────────────

    private static List<UpgradeFinding> StepChecks(DnnSiteFacts f, DnnUpgradeStep step)
    {
        var checks = new List<UpgradeFinding>();
        if (step.Method == DnnUpgradeMethod.Manual)
            checks.Add(new(UpgradeFindingSeverity.Blocking, "Method", "DNN Manager can't carry out this step",
                string.Join(" ", step.Warnings), "Upgrade the site this far by hand, following DNN's upgrade guide, then use DNN Manager for the rest."));

        foreach (var requirement in step.Requirements)
            checks.Add(requirement.Kind switch
            {
                DnnRequirementKind.NetFramework => f.NetFrameworkRelease is not { } release
                    ? new(UpgradeFindingSeverity.Unknown, "Requirement", $"{requirement.Text} - this PC's .NET Framework couldn't be read")
                    : release >= requirement.Minimum
                        ? new(UpgradeFindingSeverity.Compatible, "Requirement", $"{requirement.Text}: {DnnSiteFacts.NetFrameworkName(release)} is installed")
                        : new(UpgradeFindingSeverity.Blocking, "Requirement", $"DNN {DnnUpgradeStep.Name(step.To)} needs {requirement.Text}",
                            $"This PC has {DnnSiteFacts.NetFrameworkName(release)}.", $"Install {requirement.Text} (or newer) and restart Windows."),
                DnnRequirementKind.SqlServer => f.SqlServerMajor is not { } major
                    ? new(UpgradeFindingSeverity.Unknown, "Requirement", $"{requirement.Text} - the SQL Server's version couldn't be read")
                    : major >= requirement.Minimum
                        ? new(UpgradeFindingSeverity.Compatible, "Requirement", $"{requirement.Text}: the database is on {DnnSiteFacts.SqlServerName(major)}")
                        : new(UpgradeFindingSeverity.Blocking, "Requirement", $"DNN {DnnUpgradeStep.Name(step.To)} needs {requirement.Text}",
                            $"The database is on {DnnSiteFacts.SqlServerName(major)} ({f.SqlServerVersion}).",
                            $"Move the database to {requirement.Text} or newer, then upgrade."),
                _ => new(UpgradeFindingSeverity.Unknown, "Requirement", requirement.Text)
            });

        // Extensions built on what this step removes.
        var theirs = f.Assemblies.Where(a => a.DnnVersion is not null).ToList();
        if (step.From < RemovedApis && step.To >= RemovedApis)
        {
            var old = theirs.Where(a => a.DnnVersion! < new Version(8, 0)).ToList();
            if (old.Count > 0)
                checks.Add(new(UpgradeFindingSeverity.Warning, "Extensions", $"{old.Count} assembly(ies) built against DNN 7 or older: {Names(old)}",
                    "DNN 9.2 removed the APIs deprecated in DNN 7 and earlier - these may use them and stop working.",
                    "Get versions for DNN 9.2 or newer from their makers, or remove them, before upgrading."));
        }
        if (step.From < Ten && step.To >= Ten)
        {
            var telerik = f.Assemblies.Where(a => a.UsesTelerik).ToList();
            if (telerik.Count > 0)
                checks.Add(new(UpgradeFindingSeverity.Blocking, "Telerik", $"{telerik.Count} assembly(ies) use Telerik: {Names(telerik)}",
                    "DNN 10 removes the Telerik assemblies DNN shipped - these extensions would stop working, and pages using them fail.",
                    "Update or remove these extensions first (DNN 9.8+: Settings → Servers → Telerik removal shows what still uses Telerik)."));
            else
                checks.Add(new(UpgradeFindingSeverity.Compatible, "Telerik", f.TelerikInBin
                    ? "Telerik.Web.UI is in bin, but no extension outside DNN uses it - DNN 10 removes it"
                    : "No Telerik dependency found"));
            var beforeNine = theirs.Where(a => a.DnnVersion! < new Version(9, 0)).ToList();
            if (beforeNine.Count > 0)
                checks.Add(new(UpgradeFindingSeverity.Warning, "Extensions", $"{beforeNine.Count} assembly(ies) built against DNN 8 or older: {Names(beforeNine)}",
                    "DNN 10 has breaking changes - check each has a version for DNN 10."));
        }
        if (step.From < new Version(9, 8, 0) && step.To >= new Version(9, 8, 0) && step.To < Ten && f.Assemblies.Any(a => a.UsesTelerik))
            checks.Add(new(UpgradeFindingSeverity.Warning, "Telerik", "Extensions still use Telerik",
                "From DNN 9.8 on, removing Telerik is recommended - DNN 10 removes it, and these extensions with it."));

        foreach (var warning in step.Warnings)
            if (step.Method != DnnUpgradeMethod.Manual) checks.Add(new(UpgradeFindingSeverity.Warning, "Note", warning));
        foreach (var breaking in step.BreakingChanges)
            checks.Add(new(UpgradeFindingSeverity.Warning, "Breaking change", breaking));
        foreach (var issue in step.KnownIssues)
            checks.Add(new(UpgradeFindingSeverity.Warning, "Known issue", issue));
        foreach (var tested in step.Tested)
            checks.Add(new(UpgradeFindingSeverity.Compatible, "Tested", tested));
        return checks;

        static string Names(List<DnnAssembly> assemblies) =>
            string.Join(", ", assemblies.Take(8).Select(a => $"{a.File} (DNN {a.DnnVersion?.ToString(3) ?? "-"})")) + (assemblies.Count > 8 ? ", …" : "");
    }

    private static Version? DnnInstallVersion(string version) => UseCases.DnnInstall.Number(version);
}
