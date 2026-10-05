using DnnManager.Application.Abstractions;

namespace DnnManager.Application.Upgrades;

/// <summary>An account the checks sign in with after each step - never kept: asked for when the upgrade starts.</summary>
public sealed record DnnTestAccount(string UserName, string Password, bool IsHost);

/// <summary>A page the checks open, and what it showed - null when it was fine.</summary>
public sealed record DnnPageState(string Portal, string Url, string? Problem);

/// <summary>
/// What a site was like right before a step: its counts, its extensions and its pages - what the checks after the step
/// compare with, so only what the step changed counts against it.
/// </summary>
public sealed record DnnSiteState(DnnContentCounts? Counts, IReadOnlyList<string> Extensions, IReadOnlyList<DnnPageState> Pages,
    IReadOnlyList<DnnContentItem> Items);

/// <summary>
/// What became of a site's pages, modules and scheduled jobs in an upgrade step, compared one by one: those lost, those
/// DNN's upgrade removed as its own (DNN 10 removes the Digital Assets Manager and its Telerik extensions; DNN 8 and 9
/// replace its Admin and Host pages - tested: 7.4.2 → 8.0.4 removed ten of them), and permissions that are fewer on what
/// is still there.
/// </summary>
public static class DnnContentComparison
{
    public sealed record Outcome(IReadOnlyList<DnnContentItem> Lost, IReadOnlyList<DnnContentItem> RemovedWithExtension, IReadOnlyList<string> FewerPermissions,
        IReadOnlyList<DnnContentItem> JobsGone);

    public static Outcome Compare(DnnSiteState before, IReadOnlyList<DnnContentItem> after, IReadOnlyCollection<string> extensionsAfter)
    {
        var now = after.ToDictionary(i => (i.Kind, i.Id));
        // What modules can still be made of: the packages, and the module types (DNN's own may have no package).
        var extensions = new HashSet<string>(extensionsAfter.Concat(after.Where(i => i.Kind == "type").Select(i => i.Name)), StringComparer.OrdinalIgnoreCase);
        var gone = before.Items.Where(i => i.Kind != "type" && !now.ContainsKey((i.Kind, i.Id))).ToList();
        // A module whose extension the upgrade removed went with it; a page went with them when all of its modules did.
        var withExtension = gone.Where(i => i.Kind == "module" && i.Package is { } p && !extensions.Contains(p)).ToList();
        var pagesWith = gone.Where(i => i.Kind == "page" &&
                                        before.Items.Where(m => m.Kind == "module" && m.PageId == i.Id) is var modules && modules.Any() &&
                                        modules.All(m => withExtension.Contains(m))).ToList();
        // DNN's own Admin and Host pages, and the modules on them (the console modules on //Admin and //Host themselves
        // too): DNN 8 moved some into its new admin, DNN 9 put the Persona Bar in their place - tested: 8.0.4 → 9.1.1.
        // A site's own pages aren't under //Admin or //Host.
        var adminPages = gone.Where(i => i.Kind == "page" && IsDnnAdmin(i.Name)).ToList();
        var adminIds = before.Items.Where(i => i.Kind == "page" && IsDnnAdmin(i.Name)).Select(i => i.Id).ToHashSet();
        var onAdminPages = gone.Where(i => i.Kind == "module" && i.PageId is { } page && adminIds.Contains(page)).ToList();
        // DNN's Console module holds nothing of its own - it lists a page's child pages. DNN 9's upgrade removes those its
        // site template put on the Activity Feed pages ("Navigation") - tested: 8.0.4 → 9.1.1.
        var consoles = gone.Where(i => i.Kind == "module" && string.Equals(i.Package, "DotNetNuke.Console", StringComparison.OrdinalIgnoreCase)).ToList();
        var expected = withExtension.Concat(pagesWith).Concat(adminPages).Concat(onAdminPages).Concat(consoles).ToHashSet();
        var jobs = gone.Where(i => i.Kind == "job").ToList();
        var lost = gone.Where(i => i.Kind != "job" && !expected.Contains(i)).ToList();
        var fewer = before.Items.Where(i => i.Kind != "type" && now.TryGetValue((i.Kind, i.Id), out var n) && n.Permissions < i.Permissions)
            .Select(i => $"{i.Kind} {i.Name}: {i.Permissions} → {now[(i.Kind, i.Id)].Permissions}").ToList();
        return new Outcome(lost, expected.ToList(), fewer, jobs);
    }

    /// <summary>A page in DNN's Admin or Host menu, by its path: //Admin, //Admin//LogViewer, //Host//SQL.</summary>
    private static bool IsDnnAdmin(string tabPath) =>
        tabPath.Equals("//Admin", StringComparison.OrdinalIgnoreCase) || tabPath.Equals("//Host", StringComparison.OrdinalIgnoreCase) ||
        tabPath.StartsWith("//Admin//", StringComparison.OrdinalIgnoreCase) || tabPath.StartsWith("//Host//", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The checks after each upgrade step, and the diagnostics when one fails. A step passes when nothing is blocking: the
/// database at the target version, the same portals, users, roles, pages, modules, permissions and scheduled jobs, every
/// portal's home page and the sampled pages answering without an error that wasn't there before, and each account given
/// signing in (the host seeing its Persona Bar). New errors in DNN's logs and Windows' event logs are warnings.
/// </summary>
public interface IDnnUpgradeChecks
{
    /// <summary>The site as it is now - taken right before a step.</summary>
    Task<DnnSiteState> CaptureAsync(DnnSiteAddress site, DatabaseConnection database, CancellationToken ct);

    /// <summary>
    /// The site after a step to <paramref name="expected"/>, against <paramref name="before"/>; each check reported as it
    /// goes. What DNN and Windows logged since <paramref name="sinceUtc"/> is looked at too.
    /// </summary>
    Task<IReadOnlyList<UpgradeFinding>> ValidateAsync(DnnSiteAddress site, DatabaseConnection database, DnnSiteState before, Version expected,
        DateTime sinceUtc, IReadOnlyList<DnnTestAccount> accounts, string appPool, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Keeps what tells why a step failed in <paramref name="folder"/>: DNN's logs and its database scripts' logs written
    /// since <paramref name="sinceUtc"/>, and the site's and its app pool's Windows events. What it found, as lines.
    /// </summary>
    Task<IReadOnlyList<string>> CollectDiagnosticsAsync(string siteDirectory, string appPool, DateTime sinceUtc, string folder, CancellationToken ct);
}
