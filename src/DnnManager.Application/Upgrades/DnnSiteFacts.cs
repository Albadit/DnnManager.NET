using DnnManager.Application.Abstractions;

namespace DnnManager.Application.Upgrades;

/// <summary>An extension installed in DNN (its <c>Packages</c> table): module, skin, provider, library…</summary>
/// <param name="Type">DNN's package type: Module, Skin, Container, Provider, Auth_System, Library, …</param>
/// <param name="IsDnns">Part of DNN itself (a system package, or DNN's own organisation) - upgraded with it.</param>
public sealed record DnnExtension(string Name, string FriendlyName, string Type, string Version, string? Owner, bool IsDnns);

/// <summary>
/// An assembly in the site's <c>bin</c> that isn't DNN's own - a module's, a skin's, the site's own code - and what it was
/// built against, read from its references (nothing is loaded).
/// </summary>
/// <param name="DnnVersion">The DotNetNuke.dll version it references; null when it doesn't reference DNN.</param>
/// <param name="UsesTelerik">It references Telerik.Web.UI or DNN's Telerik wrappers (DotNetNuke.Web.Deprecated, DotNetNuke.Website.Deprecated).</param>
/// <param name="Registered">An installed extension registered it (DNN's <c>Assemblies</c> table) - otherwise it was copied in by hand.</param>
public sealed record DnnAssembly(string File, string Name, Version? DnnVersion, bool UsesTelerik, bool Registered);

/// <summary>A count of something an upgrade must leave as it was (portals, users, roles…), for the checks before and after each step.</summary>
public sealed record DnnContentCounts(int Portals, int Users, int Roles, int UserRoles, int Pages, int Modules, int Permissions, int ScheduledJobs, int Extensions)
{
    /// <summary>
    /// The portals, users, roles and user roles fewer than <paramref name="before"/>, as "Users 12 → 11" - what no upgrade
    /// may change. Pages, modules, permissions and scheduled jobs are compared one by one (<see cref="DnnContentItem"/>):
    /// DNN's upgrade removes some of its own with their extensions.
    /// </summary>
    public IReadOnlyList<string> Lost(DnnContentCounts before)
    {
        var lost = new List<string>();
        void Check(string name, int was, int now) { if (now < was) lost.Add($"{name} {was} → {now}"); }
        Check("Portals", before.Portals, Portals);
        Check("Users", before.Users, Users);
        Check("Roles", before.Roles, Roles);
        Check("User roles", before.UserRoles, UserRoles);
        return lost;
    }

    public override string ToString() =>
        $"{Portals} portal(s), {Users} users, {Roles} roles, {UserRoles} user roles, {Pages} pages, {Modules} modules, " +
        $"{Permissions} permissions, {ScheduledJobs} scheduled jobs, {Extensions} extensions";
}

/// <summary>
/// A page, module or scheduled job of a site - what the checks after an upgrade step compare one by one.
/// </summary>
/// <param name="Kind">"page", "module" or "job".</param>
/// <param name="Name">How people know it: the page's path, the module's title and page, the job's type.</param>
/// <param name="Package">A module's extension (DNN's <c>Packages</c>) - when the upgrade removed that extension, the module goes with it.</param>
/// <param name="PageId">The page a module is on.</param>
/// <param name="Permissions">The permissions set on it.</param>
public sealed record DnnContentItem(string Kind, int Id, string Name, string? Package = null, int? PageId = null, int Permissions = 0);

/// <summary>
/// What the pre-upgrade analyser read from a site, its server and this PC - everything the upgrade plan is judged on.
/// A fact that couldn't be read is null (or its problem is said), and its check comes out Unknown.
/// </summary>
public sealed record DnnSiteFacts
{
    public required string SiteName { get; init; }
    public required string Directory { get; init; }

    // ── DNN ──
    /// <summary>bin\DotNetNuke.dll's version.</summary>
    public string? FilesVersion { get; init; }
    /// <summary>The newest version in the database's <c>Version</c> table.</summary>
    public string? DatabaseVersion { get; init; }
    /// <summary>installBlocker.lock is there: an installation or upgrade is running, or one was cut off.</summary>
    public bool InstallBlocked { get; init; }

    // ── This PC and IIS ──
    /// <summary>The .NET Framework 4.x installed (the v4 Full key's <c>Release</c>: 528040+ is 4.8).</summary>
    public int? NetFrameworkRelease { get; init; }
    public string? AppPoolClr { get; init; }
    public string? AppPoolPipeline { get; init; }
    /// <summary>The site has an http binding with a host name - DNN's upgrade is run through it.</summary>
    public bool HasHostBinding { get; init; }
    public long SiteBytes { get; init; }
    /// <summary>Free space on the drive the backups are written to.</summary>
    public long? BackupFreeBytes { get; init; }

    // ── The database ──
    public DatabaseKind? DatabaseKind { get; init; }
    public string? DatabaseName { get; init; }
    /// <summary>Why the database couldn't be read; null when it was.</summary>
    public string? DatabaseProblem { get; init; }
    /// <summary>SQL Server's version, e.g. 16.0.4135.4.</summary>
    public string? SqlServerVersion { get; init; }
    public string? SqlServerEdition { get; init; }
    /// <summary>The database's state - ONLINE when it is usable.</summary>
    public string? DatabaseState { get; init; }
    public DnnContentCounts? Counts { get; init; }
    public IReadOnlyList<DnnExtension> Extensions { get; init; } = [];

    // ── The site's files ──
    public IReadOnlyList<DnnAssembly> Assemblies { get; init; } = [];
    /// <summary>Telerik.Web.UI.dll is in bin.</summary>
    public bool TelerikInBin { get; init; }

    // ── web.config ──
    public bool? HasMachineKey { get; init; }
    /// <summary>The AutoUpgrade appSetting - null when there is none.</summary>
    public string? AutoUpgrade { get; init; }
    /// <summary>appSettings DNN doesn't ship - the site's own customisations, kept by the upgrade.</summary>
    public IReadOnlyList<string> CustomAppSettings { get; init; } = [];
    public IReadOnlyList<string> ConnectionStrings { get; init; } = [];

    /// <summary>SQL Server's major version: 16 for 16.0.4135.4 (SQL Server 2022).</summary>
    public int? SqlServerMajor => SqlServerVersion is { } v && int.TryParse(v.Split('.')[0], out var major) ? major : null;

    /// <summary>"SQL Server 2022" for major 16.</summary>
    public static string SqlServerName(int major) => major switch
    {
        8 => "SQL Server 2000", 9 => "SQL Server 2005", 10 => "SQL Server 2008", 11 => "SQL Server 2012", 12 => "SQL Server 2014",
        13 => "SQL Server 2016", 14 => "SQL Server 2017", 15 => "SQL Server 2019", 16 => "SQL Server 2022", 17 => "SQL Server 2025",
        _ => $"SQL Server (version {major})"
    };

    /// <summary>".NET Framework 4.8.1" for the v4 Full key's <c>Release</c>.</summary>
    public static string NetFrameworkName(int release) => release switch
    {
        >= 533320 => ".NET Framework 4.8.1",
        >= 528040 => ".NET Framework 4.8",
        >= 461808 => ".NET Framework 4.7.2",
        >= 461308 => ".NET Framework 4.7.1",
        >= 460798 => ".NET Framework 4.7",
        >= 394802 => ".NET Framework 4.6.2",
        >= 394254 => ".NET Framework 4.6.1",
        >= 393295 => ".NET Framework 4.6",
        >= 379893 => ".NET Framework 4.5.2",
        >= 378675 => ".NET Framework 4.5.1",
        >= 378389 => ".NET Framework 4.5",
        _ => $".NET Framework 4 (release {release})"
    };
}

/// <summary>Reads <see cref="DnnSiteFacts"/> - the pre-upgrade analyser's eyes.</summary>
public interface IDnnSiteInspector
{
    /// <summary>
    /// Everything that can be read about the site <paramref name="siteName"/> in <paramref name="directory"/>, with its
    /// database <paramref name="database"/> (null when web.config names none). Never throws: what can't be read is left
    /// out and said.
    /// </summary>
    Task<DnnSiteFacts> InspectAsync(string siteName, string directory, DatabaseConnection? database, CancellationToken ct);

    /// <summary>The counts an upgrade must leave as they were; null when the database can't be read.</summary>
    Task<DnnContentCounts?> CountAsync(string directory, DatabaseConnection database, CancellationToken ct);
}
