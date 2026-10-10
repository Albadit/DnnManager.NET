using System.Globalization;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Diagnostics;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>Something wrong or unusual about a site - always from a value that was detected, with that value.</summary>
public sealed record DetectedIssue(Health Health, string Area, string Text);

/// <summary>
/// Everything read about a site for its Details page - each part from where it really is: IIS, the folder, web.config,
/// bin, the database (with web.config's connection). What DNN Manager saved at setup is only used to find it.
/// </summary>
public sealed record ProjectSnapshot
{
    public required ProjectRow Row { get; init; }
    public required DnnProject Project { get; init; }
    public IisSiteDetails? Iis { get; init; }
    public required WebConfigInspection Config { get; init; }
    public IReadOnlyList<FolderCheck> Folders { get; init; } = [];
    public AssemblyInspection Assemblies { get; init; } = AssemblyInspection.None;
    public string? DnnProduct { get; init; }
    /// <summary>The database as web.config connects to it; null when it names none.</summary>
    public DatabaseConnection? Connection { get; init; }
    /// <summary>What the database said; null while it is read, or when there is none to read.</summary>
    public DatabaseInspection? Database { get; init; }
    public bool DatabaseRead { get; init; }
    public ProjectRecord? Record { get; init; }
    public Result<IReadOnlyList<DnnHostAccount>>? Hosts { get; init; }
    /// <summary>The folder's git branch, solutions and backups - read with the rest of the folder (<see cref="ProjectDiagnostics.ReadFolderFacts"/>).</summary>
    public FolderFacts Folder { get; init; } = FolderFacts.None;
}

/// <summary>What the project's folder says about how it is worked on: its git branch, its solutions, its backups.</summary>
public sealed record FolderFacts(string? GitBranch, IReadOnlyList<string> Solutions, IReadOnlyList<ProjectBackup> Backups)
{
    public static readonly FolderFacts None = new(null, [], []);
}

/// <summary>Turns a <see cref="ProjectSnapshot"/> into the Details page's tabs - and finds what is wrong in it.</summary>
public static class ProjectDiagnostics
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    // ─── General ──────────────────────────────────────────────────────────

    public static IEnumerable<InspectorSection> General(ProjectSnapshot s, IReadOnlyList<DetectedIssue> issues)
    {
        var p = s.Row.Project;
        var project = new InspectorSection("Project", "the IIS site, its folder and bin");
        project.Add("Name", s.Row.Name);
        project.Add("Physical path", p.Directory, Directory.Exists(p.Directory) ? Health.None : Health.Bad,
            !Directory.Exists(p.Directory) ? "The folder IIS serves isn't there." : p.InProjectsFolder ? null : "Outside the projects folder.");
        project.Add("Folder size", s.Row.Size);
        project.Add("Type", ProjectType(s));
        if (s.Row.IsDnn) project.Add("DNN version", $"{p.DnnVersion}{(s.DnnProduct is { } product ? $" ({product})" : "")}", detail: @"From bin\DotNetNuke.dll.");
        if (s.Database?.Dnn?.Installed is { } installed) project.Add("Installed", installed.ToString("g", Culture), detail: "The first entry of DNN's Version table.");
        else if (s.Config.InstallationDate is { } date) project.Add("Installed", date, detail: "InstallationDate in web.config.");
        if (s.Assemblies.Newest is { } newest)
            project.Add("Last deployed (bin)", newest.Modified.ToString("g", Culture), detail: $"The newest assembly: {newest.File}.");
        if (s.Config.Modified is { } changed) project.Add("web.config changed", changed.ToString("g", Culture));
        if (s.Folder.GitBranch is { } branch) project.Add("Git branch", branch);
        if (s.Folder.Solutions is { Count: > 0 } solutions) project.Add("Solution", string.Join(", ", solutions));
        var backups = s.Folder.Backups;
        project.Add("Backups", backups.Count == 0 ? "none" : $"{backups.Count} - newest {backups[0].Created:g}", detail: s.Project.BackupDirectory);
        yield return project;

        yield return HealthSection(s, issues);
    }

    /// <summary>Each part a DNN site needs - found, and as it should be, or what was found instead.</summary>
    private static InspectorSection HealthSection(ProjectSnapshot s, IReadOnlyList<DetectedIssue> issues)
    {
        var p = s.Row.Project;
        var section = new InspectorSection("Project health");
        if (s.Row.IsDnn)
            section.Add("DNN installation", s.Config.HasDnnSection ? $"DNN {p.DnnVersion} - bin\\DotNetNuke.dll and web.config's dotnetnuke section"
                    : "bin\\DotNetNuke.dll, but web.config has no dotnetnuke section",
                s.Config.HasDnnSection ? Projects.Health.Ok : Projects.Health.Bad);
        else section.Add("DNN installation", @"none - no bin\DotNetNuke.dll");

        section.Add("web.config", s.Config.Problem is { } problem ? problem : "found and read", s.Config.Problem is null ? Projects.Health.Ok : Projects.Health.Bad,
            s.Config.Path);

        if (s.Connection is { } c)
            section.Add("Database configuration", $"[{c.Database}] on {c.Server} - {Authentication(c)}", Projects.Health.Ok, "SiteSqlServer in web.config.");
        else if (p.DatabaseProblem is { } why)
            section.Add("Database configuration", why, Projects.Health.Warning);

        if (s.Connection is not null && !p.DatabaseIsFile)
            section.Add("Database", s.Row.Sql == "Live" ? $"[{p.DatabaseName}] answers" : s.Row.SqlTip ?? s.Row.Sql,
                s.Row.Sql switch { "Live" => Projects.Health.Ok, "Offline" or "(none)" => Projects.Health.Bad, _ => Projects.Health.None });

        var site = s.Iis;
        section.Add("IIS site", site is null ? s.Row.StateText : $"{site.State} - ID {site.Id}",
            IisStates.IsStarted(site?.State) ? Projects.Health.Ok : Projects.Health.Warning);
        if (site is not null)
            section.Add("Application pool", site.Pool is { } pool ? $"{pool.Name} - {pool.State ?? "state unknown"}" : "not in IIS",
                site.Pool is null ? Projects.Health.Bad : IisStates.IsStarted(site.Pool.State) ? Projects.Health.Ok : Projects.Health.Warning);

        if (s.Database?.Dnn is { } dnn)
        {
            var aliasIssues = issues.Count(i => i.Area == "Portals");
            section.Add("Portal configuration", $"{Count(dnn.Portals.Count, "portal")}, {Count(dnn.Portals.Sum(x => x.OtherAliases.Count + (x.PrimaryAlias is null ? 0 : 1)), "alias", "aliases")}" +
                                                (aliasIssues == 0 ? " - every alias has an IIS binding" : ""),
                aliasIssues == 0 ? Projects.Health.Ok : Projects.Health.Warning,
                aliasIssues == 0 ? null : "See Detected issues on Advanced.");
        }

        var (issuesText, issuesHealth) = Tally(issues.Select(i => i.Health));
        section.Add("Detected issues", issuesText, issuesHealth, issues.Count > 0 ? "Listed at the bottom of Advanced." : null);
        // Assembly problems have a list of their own - in Assemblies on Advanced, not in Detected issues.
        if (s.Assemblies.Problems.Count > 0)
        {
            var (assembliesText, assembliesHealth) = Tally(s.Assemblies.Problems.Select(AssemblyHealth));
            section.Add("Assemblies", assembliesText, assembliesHealth, "Listed in Assemblies on Advanced.");
        }
        if (!s.DatabaseRead && s.Connection is not null) section.Note = "Reading the database…";
        return section;
    }

    // ─── IIS ──────────────────────────────────────────────────────────────

    public static IEnumerable<InspectorSection> Iis(ProjectSnapshot s)
    {
        if (s.Iis is not { } site)
        {
            yield return new InspectorSection("Site", "IIS") { Note = "IIS's configuration couldn't be read - DNN Manager reads it with administrator rights." };
            yield break;
        }

        var siteSection = new InspectorSection("Site", "IIS - applicationHost.config");
        siteSection.Add("Name", site.Name);
        siteSection.Add("Site ID", site.Id.ToString(Culture), detail: $"Its IIS logs are in W3SVC{site.Id}.");
        siteSection.Add("Physical path", site.PhysicalPath, Directory.Exists(site.PhysicalPath) ? Projects.Health.None : Projects.Health.Bad);
        siteSection.Add("State", site.State, IisStates.IsStarted(site.State) ? Projects.Health.Ok : Projects.Health.Warning);
        siteSection.Add("Application pool", site.Pool?.Name ?? "not in IIS", site.Pool is null ? Projects.Health.Bad : Projects.Health.None);
        siteSection.Add("Configuration", site.ConfigPath);
        siteSection.Add("Auto-start", site.ServerAutoStart ? "on - starts with IIS" : "off - started by hand");
        siteSection.Add("Preload", site.PreloadEnabled switch { true => "enabled - IIS starts the application without waiting for a request", false => "disabled", null => "not available in this IIS" });
        yield return siteSection;

        var bindings = new InspectorSection("Bindings", "IIS")
        {
            Table = new InspectorTable(["Protocol", "Host", "Port", "IP address", "Certificate", "Expires", "SNI"],
                site.Bindings.Select(b =>
                {
                    var (health, detail) = CertificateHealth(b);
                    var host = b.Host.Length > 0 ? b.Host : "(any host)";
                    var url = b.Port is { } port && b.Protocol is "http" or "https"
                        ? $"{b.Protocol}://{(b.Host.Length > 0 ? b.Host : "localhost")}{(port == (b.Protocol == "https" ? 443 : 80) ? "" : $":{port}")}" : host;
                    return new InspectorTableRow(
                    [
                        b.Protocol, url, b.Port?.ToString(Culture) ?? "-", b.Address is "*" or "" ? "all" : b.Address,
                        b.Protocol != "https" ? "-" : b.Certificate is { } cert ? cert.FriendlyName ?? cert.Subject : b.CertificateHash is null ? "none" : "not found",
                        b.Certificate is { } c ? c.NotAfter.ToString("d", Culture) : "-",
                        b.Protocol != "https" ? "-" : b.Sni ? "yes" : "no"
                    ], health, detail);
                }).ToList())
        };
        if (site.Bindings.Count == 0) bindings.Note = "The site has no bindings.";
        yield return bindings;

        if (site.Pool is { } pool)
        {
            var poolSection = new InspectorSection("Application pool", "IIS");
            poolSection.Add("Name", pool.Name);
            poolSection.Add("State", pool.State ?? "unknown", IisStates.IsStarted(pool.State) ? Projects.Health.Ok : Projects.Health.Warning);
            poolSection.Add(".NET CLR", pool.Runtime);
            poolSection.Add("Pipeline mode", pool.Pipeline, pool.Pipeline == "Classic" ? Projects.Health.Warning : Projects.Health.None,
                pool.Pipeline == "Classic" ? "DNN runs in Integrated mode." : null);
            poolSection.Add("Identity", pool.IdentityType == "SpecificUser" ? pool.Account : $"{pool.IdentityType} - {pool.Account}");
            poolSection.Add("Start mode", pool.StartMode);
            poolSection.Add("Idle time-out", pool.IdleTimeout == TimeSpan.Zero ? "none" : $"{Span(pool.IdleTimeout)}{(pool.IdleTimeoutAction is { } a ? $" - then {a}" : "")}");
            poolSection.Add("Regular recycling", (pool.RecycleInterval == TimeSpan.Zero ? "off" : $"every {Span(pool.RecycleInterval)}") +
                                                 (pool.RecycleAt.Count > 0 ? $"; at {string.Join(", ", pool.RecycleAt.Select(t => t.ToString(@"hh\:mm")))}" : ""));
            poolSection.Add("Private memory limit", pool.PrivateMemoryLimitKb == 0 ? "none" : ByteSize.Format(pool.PrivateMemoryLimitKb * 1024));
            if (pool.VirtualMemoryLimitKb > 0) poolSection.Add("Virtual memory limit", ByteSize.Format(pool.VirtualMemoryLimitKb * 1024));
            poolSection.Add("Queue length", pool.QueueLength.ToString("N0", Culture));
            poolSection.Add("32-bit applications", pool.Enable32Bit ? "enabled - 32-bit worker process" : "disabled",
                pool.Enable32Bit ? Projects.Health.Warning : Projects.Health.None);
            poolSection.Add("Rapid-fail protection", pool.RapidFailProtection
                ? $"on - stopped after {pool.RapidFailMaxCrashes} crashes in {Span(pool.RapidFailInterval)}" : "off");
            poolSection.Add("Load user profile", pool.LoadUserProfile ? "yes" : "no");
            poolSection.Add("Max worker processes", pool.MaxProcesses.ToString(Culture), pool.MaxProcesses > 1 ? Projects.Health.Warning : Projects.Health.None,
                pool.MaxProcesses > 1 ? "A web garden - DNN's cache and scheduler run in each process." : null);
            yield return poolSection;
        }

        var runtime = new InspectorSection("Worker processes", "Windows");
        var pids = s.Iis.Pool?.WorkerProcessIds is { Count: > 0 } fromIis ? fromIis : s.Row.IisSite.WorkerProcessIds;
        if (pids.Count == 0) runtime.Note = "No worker process - IIS starts one on the site's first request.";
        else
        {
            runtime.Add("Count", pids.Count.ToString(Culture));
            runtime.Add("PID", string.Join(", ", pids));
            if (s.Row.Project.Stats?.Started is { } started)
                runtime.Add("Up since", $"{started:g} ({Span(DateTime.Now - started)})");
            runtime.Add("Memory", s.Row.MemoryText);
            runtime.Add("CPU", s.Row.CpuText, detail: "Of the whole PC, at the last sample.");
        }
        yield return runtime;
    }

    private static (Health, string?) CertificateHealth(IisBindingDetails b)
    {
        if (b.Protocol != "https") return (Projects.Health.None, null);
        if (b.CertificateHash is null) return (Projects.Health.Bad, "https without a certificate");
        if (b.Certificate is not { } cert) return (Projects.Health.Bad, $"Certificate {b.CertificateHash} isn't in the {b.CertificateStore} store");
        if (cert.NotAfter < DateTime.Now) return (Projects.Health.Bad, $"Expired on {cert.NotAfter:d}");
        if (cert.NotAfter < DateTime.Now.AddDays(30)) return (Projects.Health.Warning, $"Expires on {cert.NotAfter:d}");
        return (Projects.Health.Ok, $"{cert.Subject}, issued by {cert.Issuer}");
    }

    // ─── DNN ──────────────────────────────────────────────────────────────

    public static IEnumerable<InspectorSection> Dnn(ProjectSnapshot s)
    {
        var p = s.Row.Project;
        var dnn = s.Database?.Dnn;
        var install = new InspectorSection("Installation", "bin, web.config and DNN's database");
        if (!s.Row.IsDnn)
        {
            install.Note = @"No DNN here - the site's folder has no bin\DotNetNuke.dll.";
            yield return install;
            yield break;
        }
        install.Add("DNN version (files)", $"{p.DnnVersion}{(s.DnnProduct is { } product ? $" - {product}" : "")}");
        if (dnn?.Version is { } dbVersion)
        {
            var same = SameVersion(p.DnnVersion, dbVersion);
            install.Add("DNN version (database)", dbVersion, same ? Projects.Health.None : Projects.Health.Bad,
                same ? null : "The files and the database are at different versions - an upgrade didn't run or didn't finish.");
        }
        install.Add("InstallVersion", s.Config.InstallVersion, detail: "In web.config's appSettings.");
        if (dnn?.Installed is { } installed) install.Add("Installed", installed.ToString("g", Culture));
        if (dnn?.Upgraded is { } upgraded && dnn.Installed is { } first && upgraded > first.AddMinutes(5))
            install.Add("Last upgrade", upgraded.ToString("g", Culture));
        if (s.Record is { } record)
            install.Add("Set up by", record.InstallMode == DnnInstallMode.Automatic
                ? $"DNN Manager, automatic setup - {record.CreatedUtc.ToLocalTime():g}" : $"DNN Manager, DNN's installation wizard - {record.CreatedUtc.ToLocalTime():g}");
        if (dnn is not null)
        {
            install.Add("Portals", dnn.Portals.Count.ToString(Culture));
            install.Add("Scheduler", dnn.HostSettings.GetValueOrDefault("SchedulerMode") switch
            {
                "0" => "disabled",
                "1" => "timer method",
                "2" => "request method",
                null => null,
                var other => other
            }, dnn.HostSettings.GetValueOrDefault("SchedulerMode") == "0" ? Projects.Health.Warning : Projects.Health.None,
                dnn.ScheduleItems is { } jobs ? $"{jobs} jobs, {dnn.ScheduleEnabled} enabled." : null);
            install.Add("Friendly URLs", dnn.HostSettings.GetValueOrDefault("UseFriendlyUrls") is { } friendly
                ? (friendly == "Y" ? "on" : "off") + (s.Config.FriendlyUrlProvider is { } provider ? $" - {provider}{(s.Config.FriendlyUrlFormat is { } format ? $" ({format})" : "")}" : "")
                : s.Config.FriendlyUrlProvider);
            install.Add("Caching", $"{s.Config.CachingProvider ?? "-"}" + (dnn.HostSettings.GetValueOrDefault("PerformanceSetting") switch
            {
                "0" => " - no caching",
                "1" => " - light caching",
                "2" => " - moderate caching",
                "3" => " - heavy caching",
                _ => ""
            }));
            install.Add("Authentication", dnn.AuthenticationProviders.Count == 0 ? null
                : string.Join(", ", dnn.AuthenticationProviders.Select(a => a.Enabled ? a.Type : $"{a.Type} (disabled)")));
            install.Add("Event log", $"{s.Config.LoggingProvider ?? "-"}" + (dnn.EventLogRecords is { } records ? $" - {records:N0} records" : ""));
            install.Add("Debug mode", dnn.HostSettings.GetValueOrDefault("DebugMode") switch { "True" => "on", "False" => "off", _ => null },
                detail: "Host setting DebugMode.");
            install.Add("Auto-add portal alias", dnn.HostSettings.GetValueOrDefault("AutoAddPortalAlias") switch { "Y" => "on", "N" => "off", _ => null });
        }
        else install.Note = DatabaseNote(s);
        yield return install;

        if (dnn is not null)
        {
            var site = s.Iis;
            yield return new InspectorSection("Portals", "DNN's database")
            {
                Table = new InspectorTable(["ID", "Name", "Primary alias", "Other aliases", "Language", "Home directory"],
                    dnn.Portals.Select(portal =>
                    {
                        var missing = AliasesWithoutBinding(portal, site).ToList();
                        return new InspectorTableRow(
                        [
                            portal.Id.ToString(Culture), portal.Name + (portal.Expired ? " (expired)" : ""),
                            portal.PrimaryAlias is { } primary ? AliasUrl(primary, site) : "none",
                            portal.OtherAliases.Count == 0 ? "-" : string.Join("\n", portal.OtherAliases.Select(o => o.Culture is { Length: > 0 } c ? $"{o.Alias} ({c})" : o.Alias)),
                            portal.DefaultLanguage ?? "-", portal.HomeDirectory ?? "-"
                        ], missing.Count > 0 || portal.Expired ? Projects.Health.Warning : Projects.Health.None,
                            missing.Count > 0 ? $"No IIS binding for {string.Join(", ", missing)}" : null);
                    }).ToList()),
                Note = dnn.Portals.Count == 0 ? "The database has no portals." : null
            };

            var extensions = new InspectorSection("Extensions", "DNN's database");
            extensions.Add("Modules", dnn.DesktopModules?.ToString(Culture));
            foreach (var (type, label) in new[] { ("Skin", "Themes (skins)"), ("Container", "Containers"), ("Auth_System", "Authentication systems"),
                         ("Provider", "Providers"), ("Library", "Libraries"), ("JavaScript_Library", "JavaScript libraries"),
                         ("CoreLanguagePack", "Language packs (core)"), ("ExtensionLanguagePack", "Language packs (extensions)") })
                if (dnn.Packages.TryGetValue(type, out var n)) extensions.Add(label, n.ToString(Culture));
            extensions.Add("Scheduled jobs", dnn.ScheduleItems is { } total ? $"{total} - {dnn.ScheduleEnabled} enabled" : null);
            extensions.Add("Packages in all", dnn.Packages.Count == 0 ? null : dnn.Packages.Values.Sum().ToString(Culture));
            yield return extensions;
        }

        var config = new InspectorSection("DNN configuration", "web.config");
        config.Add("Compilation debug", s.Config.Debug switch { true => "true", false => "false", null => "false (not set)" },
            s.Config.Debug == true ? Projects.Health.Warning : Projects.Health.None);
        config.Add("Custom errors", s.Config.CustomErrors ?? "RemoteOnly (not set)");
        config.Add("Machine key", s.Config.MachineKey ?? "none - ASP.NET generates keys per machine",
            s.Config.MachineKey is null && s.Row.IsDnn ? Projects.Health.Warning : Projects.Health.None);
        config.Add("Friendly URL provider", s.Config.FriendlyUrlProvider is { } fp ? $"{fp}{(s.Config.FriendlyUrlFormat is { } f ? $" - urlFormat {f}" : "")}" : null);
        config.Add("Data provider", s.Config.DataProvider is { } dp
            ? $"{dp} - objectQualifier {(s.Config.ObjectQualifier is { Length: > 0 } oq ? $"'{oq}'" : "none")}, databaseOwner '{s.Config.DatabaseOwner}'" : null);
        config.Add("Session state", s.Config.SessionState);
        config.Add("Authentication mode", s.Config.AuthenticationMode);
        config.Add("Auto upgrade", s.Config.AutoUpgrade, detail: "AutoUpgrade in appSettings.");
        yield return config;
    }

    // ─── Database ─────────────────────────────────────────────────────────

    public static IEnumerable<InspectorSection> Database(ProjectSnapshot s, IReadOnlyList<DetectedIssue> issues)
    {
        var p = s.Row.Project;
        var connection = new InspectorSection("Connection", "SiteSqlServer in web.config");
        if (s.Connection is not { } c)
        {
            connection.Note = p.DatabaseProblem is { } why ? $"The database isn't known: {why}." : "The site's web.config names no SiteSqlServer database.";
            yield return connection;
            yield break;
        }
        var w = s.Config;
        connection.Add("SQL Server", c.Server);
        connection.Add("Database", c.Kind == DatabaseKind.LocalDbFile ? $@"App_Data\{c.Database} (LocalDB file)" : c.Database);
        connection.Add("Authentication", Authentication(c));
        connection.Add("Encrypt", w.SqlEncrypt ?? "False (not set)");
        connection.Add("Trust server certificate", w.SqlTrustServerCertificate?.ToString() ?? "False (not set)");
        connection.Add("Connection timeout", w.SqlConnectTimeout is { } t ? $"{t} s" : "15 s (not set)");
        connection.Add("Multiple active result sets", w.SqlMultipleActiveResultSets?.ToString() ?? "False (not set)");
        if (w.SqlApplicationName is { } app) connection.Add("Application name", app);
        if (!p.DatabaseIsFile)
            connection.Add("Status", s.Database?.Problem is { } problem ? problem : s.Row.SqlTip ?? s.Row.Sql,
                s.Database?.Problem is not null || s.Row.Sql is "Offline" or "(none)" ? Projects.Health.Bad : s.Row.Sql == "Live" ? Projects.Health.Ok : Projects.Health.None);
        connection.Note = "Read with this connection - its password is never shown.";
        yield return connection;

        if (p.DatabaseIsFile)
        {
            yield return new InspectorSection("Database") { Note = "A LocalDB file runs in the site's own LocalDB instance - DNN Manager doesn't open it while the site runs." };
            yield break;
        }
        if (!s.DatabaseRead)
        {
            yield return new InspectorSection("Database") { Note = "Reading the database…" };
            yield break;
        }
        if (s.Database is not { Problem: null } inspection) yield break;

        if (inspection.Server is { } server)
        {
            var sql = new InspectorSection("SQL Server", "SERVERPROPERTY");
            sql.Add("Instance", server.Instance);
            sql.Add("Version", $"{server.Version}{(server.Level is { } level ? $" {level}" : "")}{(SqlYear(server.MajorVersion) is { } year ? $" - SQL Server {year}" : "")}");
            sql.Add("Edition", server.Edition);
            sql.Add("Server collation", server.Collation);
            yield return sql;
        }

        if (inspection.Database is { } db)
        {
            var collationDiffers = inspection.Server?.Collation is { } sc && db.Collation is { } dc && !sc.Equals(dc, StringComparison.OrdinalIgnoreCase);
            var section = new InspectorSection("Database", "sys.databases and sys.database_files");
            section.Add("Name", db.Name);
            section.Add("State", db.State, db.State == "ONLINE" ? Projects.Health.None : Projects.Health.Bad);
            section.Add("Access", db.ReadOnly == true ? "read-only" : "read-write", db.ReadOnly == true ? Projects.Health.Bad : Projects.Health.None);
            var data = db.Files.Where(f => f.Type == "ROWS").ToList();
            var log = db.Files.Where(f => f.Type == "LOG").ToList();
            section.Add("Size", $"{db.Files.Sum(f => f.SizeMb):N1} MB", detail: $"{data.Sum(f => f.SizeMb):N1} MB data, {log.Sum(f => f.SizeMb):N1} MB log");
            foreach (var file in db.Files)
                section.Add(file.Type == "LOG" ? "Log file" : file.Type == "ROWS" ? "Data file" : file.Type,
                    $"{file.SizeMb:N1} MB{(file.UsedMb is { } used ? $", {used:N1} MB used ({(file.SizeMb > 0 ? 100 * (file.SizeMb - used) / file.SizeMb : 0):N0}% free)" : "")}",
                    detail: $"{file.Name} - grows by {file.Growth}, max {file.MaxSize} - {file.PhysicalName}");
            section.Add("Recovery model", db.RecoveryModel);
            section.Add("Compatibility level", db.CompatibilityLevel is { } level ? $"{level}{(CompatibilityYear(level) is { } y ? $" (SQL Server {y})" : "")}" : null,
                db.CompatibilityLevel < 110 ? Projects.Health.Warning : Projects.Health.None);
            section.Add("Collation", db.Collation, collationDiffers ? Projects.Health.Warning : Projects.Health.None,
                collationDiffers ? $"The server's is {inspection.Server!.Collation} - temporary tables use the server's." : null);
            section.Add("Auto shrink", OnOff(db.AutoShrink), db.AutoShrink == true ? Projects.Health.Warning : Projects.Health.None);
            section.Add("Auto close", OnOff(db.AutoClose), db.AutoClose == true ? Projects.Health.Warning : Projects.Health.None);
            section.Add("Snapshot isolation", db.SnapshotIsolation is { } si ? $"{si}{(db.ReadCommittedSnapshot == true ? ", read committed snapshot on" : "")}" : null);
            section.Add("Page verification", db.PageVerify, db.PageVerify is "NONE" ? Projects.Health.Warning : Projects.Health.None);
            section.Add("Created", db.Created?.ToString("g", Culture));
            section.Add("Signed in as", db.SignedInAs, detail: "The login web.config's connection signs in with.");
            yield return section;
        }

        if (inspection.Dnn is { } dnn)
        {
            var section = new InspectorSection("DNN database", "DNN's tables");
            section.Add("DNN version", dnn.Version, SameVersion(p.DnnVersion, dnn.Version) ? Projects.Health.None : Projects.Health.Bad);
            section.Add("Object qualifier", dnn.Qualifier is { } q ? $"'{q}'" : "none");
            section.Add("Portals", dnn.Portals.Count.ToString(Culture));
            section.Add("Users", dnn.Users?.ToString("N0", Culture));
            section.Add("Roles", dnn.Roles?.ToString("N0", Culture));
            section.Add("Scheduled jobs", dnn.ScheduleItems is { } jobs ? $"{jobs} - {dnn.ScheduleEnabled} enabled" : null);
            section.Add("Event log", dnn.EventLogRecords is { } records ? $"{records:N0} records" : null,
                detail: dnn.EventLogOldest is { } oldest ? $"{oldest:g} - {dnn.EventLogNewest:g}" : null);
            yield return section;
        }
        else if (inspection.Notes.Count == 0)
        {
            yield return new InspectorSection("DNN database") { Note = "The database has no DNN tables (no Version table)." };
        }

        var health = new InspectorSection("Database health");
        var dbIssues = issues.Where(i => i.Area == "Database").ToList();
        if (dbIssues.Count == 0) health.Add("Issues", "none detected", Projects.Health.Ok);
        else health.Table = new InspectorTable(["Issue"], dbIssues.Select(i => new InspectorTableRow([i.Text], i.Health)).ToList());
        if (inspection.Notes.Count > 0) health.Note = string.Join(" ", inspection.Notes);
        yield return health;
    }

    // ─── Advanced ─────────────────────────────────────────────────────────

    public static IEnumerable<InspectorSection> Advanced(ProjectSnapshot s, IReadOnlyList<DetectedIssue> issues, IReadOnlyList<string> disabledHttpsRules)
    {
        var w = s.Config;
        var config = new InspectorSection("Configuration", w.Path);
        if (w.Problem is { } problem)
        {
            config.Add("web.config", problem, Projects.Health.Bad);
        }
        else
        {
            config.Add("Target framework", w.RuntimeTargetFramework is null && w.CompilationTargetFramework is null ? null
                : $"httpRuntime {w.RuntimeTargetFramework ?? "-"}, compilation {w.CompilationTargetFramework ?? "-"}");
            config.Add("Request validation mode", w.RequestValidationMode);
            config.Add("Request filtering", w.MaxUrl is null && w.MaxQueryString is null ? "IIS defaults (URL 4096, query string 2048)"
                : $"max URL {w.MaxUrl?.ToString(Culture) ?? "4096"}, max query string {w.MaxQueryString?.ToString(Culture) ?? "2048"}");
            config.Add("Compression", w.StaticCompression is null && w.DynamicCompression is null ? "IIS defaults (static on, dynamic off)"
                : $"static {OnOff(w.StaticCompression ?? true)}, dynamic {OnOff(w.DynamicCompression ?? false)}");
            config.Add("Static content cache", w.ClientCache ?? "not set");
            config.Add("HTTP errors", w.HttpErrors ?? "DetailedLocalOnly (not set)");
            config.Add("All managed modules for all requests", w.RunAllManagedModules?.ToString());
            config.Add("URL rewrite rules", w.RewriteRules > 0 ? w.RewriteRules.ToString(Culture) : "none");
            if (disabledHttpsRules.Count > 0)
                config.Add("HTTPS redirects", $"switched off for local use: {string.Join(", ", disabledHttpsRules)}", Projects.Health.Warning,
                    "Switch them back on before deploying.");
            config.Add("Binding redirects", w.BindingRedirects.Count.ToString(Culture));
            if (w.CodeBases.Count > 0) config.Add("Code bases", w.CodeBases.Count.ToString(Culture), detail: "Assemblies loaded from a file of their own (bin\\Imageflow, say).");
            config.Add("Probing path", w.ProbingPath);
            if (w.ExternalSections.Count > 0)
                config.Add("In files of their own", string.Join(", ", w.ExternalSections), detail: "configSource - not read here.");
        }
        yield return config;

        var limits = new InspectorSection("Limits", "web.config and DNN's host settings");
        var aspNet = (long)(w.MaxRequestLengthKb ?? WebConfigInspection.DefaultMaxRequestLengthKb) * 1024;
        var iis = w.MaxAllowedContentLength ?? WebConfigInspection.DefaultMaxAllowedContentLength;
        long? dnnLimit = s.Database?.Dnn?.HostSettings.GetValueOrDefault("MaxUploadSize") is { } setting && long.TryParse(setting, out var bytes) ? bytes : null;
        var effective = new[] { aspNet, iis, dnnLimit ?? long.MaxValue }.Min();
        var conflict = new[] { aspNet, iis }.Concat(dnnLimit is { } d ? [d] : []).Distinct().Count() > 1;
        if (dnnLimit is { } dl) limits.Add("DNN upload limit", ByteSize.Format(dl), detail: "Host setting MaxUploadSize.");
        limits.Add("ASP.NET maxRequestLength", $"{ByteSize.Format(aspNet)}{(w.MaxRequestLengthKb is null ? " (default)" : "")}",
            conflict && aspNet == effective ? Projects.Health.Warning : Projects.Health.None);
        limits.Add("IIS maxAllowedContentLength", $"{ByteSize.Format(iis)}{(w.MaxAllowedContentLength is null ? " (default)" : "")}",
            conflict && iis == effective ? Projects.Health.Warning : Projects.Health.None);
        limits.Add("Effective upload limit", ByteSize.Format(effective), conflict ? Projects.Health.Warning : Projects.Health.Ok,
            conflict ? "The limits differ - the smallest one wins." : null);
        limits.Add("Execution timeout", $"{w.ExecutionTimeoutSeconds ?? WebConfigInspection.DefaultExecutionTimeoutSeconds} s{(w.ExecutionTimeoutSeconds is null ? " (default)" : "")}",
            detail: w.Debug == true ? "Not applied while compilation debug is true." : null);
        yield return limits;

        var account = s.Iis?.Pool?.Account;
        yield return new InspectorSection("Filesystem", account is null ? "folder permissions" : $"permissions for {account}")
        {
            Table = new InspectorTable(["Folder", "Exists", account is null ? "Writable" : "Writable by the app pool", "Read-only"],
                s.Folders.Select(f => new InspectorTableRow(
                [
                    f.Folder, f.Exists ? "yes" : "no",
                    !f.Exists ? "-" : f.Writable switch { true => $"yes - {f.Via}", false => $"no - {f.Via}", null => f.Via ?? "unknown" },
                    !f.Exists ? "-" : f.ReadOnly ? "yes" : "no"
                ], !f.Exists && f.Required ? Projects.Health.Bad
                    : f.Exists && f.NeedsWrite && f.Writable == false ? Projects.Health.Bad
                    : f.ReadOnly ? Projects.Health.Warning : Projects.Health.None)).ToList()),
            Note = account is null ? "The app pool's account isn't known (IIS couldn't be read) - only whether the folders are there." : null
        };

        var assemblies = new InspectorSection("Assemblies", "bin and its folders, the probing path and codeBase");
        var a = s.Assemblies;
        // bin first, then its folders: "bin 186, bin\2sxc 1, bin\Imageflow 12".
        var folders = a.Assemblies.GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Equals("bin", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        assemblies.Add("Assemblies", $"{a.Assemblies.Count}{(a.NativeFiles > 0 ? $" (+ {a.NativeFiles} native DLLs)" : "")}",
            detail: folders.Count > 1 ? string.Join(", ", folders.Select(g => $"{g.Key} {g.Count()}")) : null);
        var own = folders.Select(g => (Folder: g.Key, Count: g.Count(x => !x.Loaded))).Where(f => f.Count > 0).ToList();
        if (own.Count > 0)
            assemblies.Add("Not loaded by ASP.NET", $"{own.Sum(f => f.Count)} - {string.Join(", ", own.Select(f => $"{f.Folder} {f.Count}"))}",
                detail: "In a folder .NET doesn't look in, and no codeBase in web.config points at them - only what uses that folder " +
                        @"loads them (a module, or the compiler in bin\roslyn).");
        var dnnCore = a.Assemblies.FirstOrDefault(x => x.Loaded && x.Name.Equals("DotNetNuke", StringComparison.OrdinalIgnoreCase));
        // DNN's own (DotNetNuke.*) - modules and libraries (Dnn.Modules.*) have versions of their own.
        var dnnAssemblies = a.Assemblies.Where(x => x.Loaded && x.Name.StartsWith("DotNetNuke", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dnnCore is not null)
        {
            var others = dnnAssemblies.Where(x => x.Version.Major != dnnCore.Version.Major || x.Version.Minor != dnnCore.Version.Minor).ToList();
            assemblies.Add("DNN assemblies", $"{dnnAssemblies.Count} DotNetNuke.* - DotNetNuke.dll {dnnCore.Version}",
                detail: others.Count == 0 ? "All at the same version." : $"At another version: {string.Join(", ", others.Select(o => $"{o.Name} {o.Version}"))}");
        }
        if (a.Problems.Count == 0) assemblies.Add("Conflicts", "none detected", Projects.Health.Ok);
        else
            assemblies.Table = new InspectorTable(["Kind", "Detected"], a.Problems.Select(x => new InspectorTableRow(
                [KindText(x.Kind), x.Text], AssemblyHealth(x))).ToList());
        yield return assemblies;

        var all = new InspectorSection("Detected issues", "everything above but the assemblies");
        if (issues.Count == 0) all.Add("Issues", "none detected", Projects.Health.Ok);
        else all.Table = new InspectorTable(["Area", "Issue"], issues.Select(i => new InspectorTableRow([i.Area, i.Text], i.Health)).ToList());
        if (!s.DatabaseRead && s.Connection is not null) all.Note = "The database is still being read - its issues follow.";
        yield return all;
    }

    // ─── Detected issues ──────────────────────────────────────────────────

    /// <summary>What is wrong or unusual - each from a detected value, which it names.</summary>
    public static IReadOnlyList<DetectedIssue> Issues(ProjectSnapshot s)
    {
        var list = new List<DetectedIssue>();
        void Add(Health h, string area, string text) => list.Add(new DetectedIssue(h, area, text));
        var p = s.Row.Project;
        var w = s.Config;

        // IIS
        if (s.Iis is { } site)
        {
            if (!Directory.Exists(site.PhysicalPath)) Add(Projects.Health.Bad, "IIS", $"The physical path {site.PhysicalPath} doesn't exist.");
            if (site.Pool is null) Add(Projects.Health.Bad, "IIS", "The site's application pool isn't in IIS.");
            else
            {
                if (IisStates.IsStarted(site.State) && site.Pool.State is { } state && !IisStates.IsStarted(state))
                    Add(Projects.Health.Bad, "IIS", $"The site is started, but its application pool '{site.Pool.Name}' is {state}.");
                if (site.Pool.Enable32Bit) Add(Projects.Health.Warning, "IIS", $"Application pool '{site.Pool.Name}' runs 32-bit worker processes (enable32BitAppOnWin64).");
                if (site.Pool.Pipeline == "Classic") Add(Projects.Health.Warning, "IIS", $"Application pool '{site.Pool.Name}' uses the Classic pipeline - DNN runs in Integrated mode.");
                if (s.Row.IsDnn && site.Pool.Runtime != "v4.0") Add(Projects.Health.Bad, "IIS", $"Application pool '{site.Pool.Name}' runs .NET CLR {site.Pool.Runtime} - DNN needs v4.0.");
                if (site.Pool.MaxProcesses > 1) Add(Projects.Health.Warning, "IIS", $"Application pool '{site.Pool.Name}' is a web garden ({site.Pool.MaxProcesses} worker processes).");
            }
            foreach (var b in site.Bindings.Where(b => b.Protocol == "https"))
            {
                var host = b.Host.Length > 0 ? b.Host : "(any host)";
                if (b.CertificateHash is null) Add(Projects.Health.Bad, "IIS", $"https binding {host}:{b.Port} has no certificate.");
                else if (b.Certificate is null) Add(Projects.Health.Bad, "IIS", $"https binding {host}:{b.Port} uses certificate {b.CertificateHash}, which isn't in the {b.CertificateStore} store.");
                else if (b.Certificate.NotAfter < DateTime.Now) Add(Projects.Health.Bad, "IIS", $"https binding {host}:{b.Port} uses an expired certificate ({b.Certificate.Subject}, expired {b.Certificate.NotAfter:d}).");
                else if (b.Certificate.NotAfter < DateTime.Now.AddDays(30)) Add(Projects.Health.Warning, "IIS", $"The certificate of https binding {host}:{b.Port} expires on {b.Certificate.NotAfter:d}.");
            }
        }

        // Portals against the bindings
        if (s.Database?.Dnn is { } dnn && s.Iis is { } iisSite)
        {
            foreach (var portal in dnn.Portals)
                foreach (var alias in AliasesWithoutBinding(portal, iisSite))
                    Add(Projects.Health.Warning, "Portals", $"Portal alias {alias} (portal {portal.Id}) has no IIS binding - its requests don't reach this site.");
            var autoAdd = dnn.HostSettings.GetValueOrDefault("AutoAddPortalAlias") == "Y";
            var aliasHosts = dnn.Portals.SelectMany(x => x.OtherAliases.Select(o => o.Alias).Append(x.PrimaryAlias ?? ""))
                .Select(AliasHost).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!autoAdd)
                foreach (var b in iisSite.Bindings.Where(b => b.Protocol is "http" or "https" && b.Host.Length > 0 && !aliasHosts.Contains(b.Host)))
                    Add(Projects.Health.Warning, "Portals", $"IIS binding {b.Protocol}://{b.Host} has no portal alias - DNN redirects its requests to a portal's alias (AutoAddPortalAlias is off).");
        }

        // web.config
        if (w.Problem is { } problem) Add(Projects.Health.Bad, "web.config", $"web.config {problem}.");
        else
        {
            if (s.Row.IsDnn && !w.HasDnnSection) Add(Projects.Health.Bad, "web.config", "bin\\DotNetNuke.dll is there, but web.config has no dotnetnuke section.");
            if (w.Debug == true) Add(Projects.Health.Warning, "web.config", "compilation debug=\"true\" - pages are compiled for debugging, and executionTimeout doesn't apply.");
            if (s.Row.IsDnn && w.MachineKey is null) Add(Projects.Health.Warning, "web.config", "No machineKey - ASP.NET generates keys per machine; sign-ins and DNN's encrypted values break when they change.");
            var aspNet = (long)(w.MaxRequestLengthKb ?? WebConfigInspection.DefaultMaxRequestLengthKb) * 1024;
            var iis = w.MaxAllowedContentLength ?? WebConfigInspection.DefaultMaxAllowedContentLength;
            if (Math.Abs(aspNet - iis) > Math.Max(aspNet, iis) / 100)
                Add(Projects.Health.Warning, "Limits", $"Upload limits differ: ASP.NET maxRequestLength {ByteSize.Format(aspNet)}, IIS maxAllowedContentLength {ByteSize.Format(iis)} - " +
                                                         $"the effective limit is {ByteSize.Format(Math.Min(aspNet, iis))}.");
        }

        // Database
        if (s.Connection is not null && !p.DatabaseIsFile)
        {
            if (s.Database?.Problem is { } why) Add(Projects.Health.Bad, "Database", $"The SQL Server doesn't answer web.config's connection: {why}");
            else if (p is { SqlReachable: true, DatabaseExists: false }) Add(Projects.Health.Bad, "Database", $"Database [{p.DatabaseName}] from web.config doesn't exist on {p.DatabaseServer}.");
        }
        if (s.Database is { Problem: null } inspection)
        {
            if (inspection.Dnn?.Version is { } dbVersion && s.Row.IsDnn && !SameVersion(p.DnnVersion, dbVersion))
                Add(Projects.Health.Bad, "Database", $"DNN's files are {p.DnnVersion}, its database {dbVersion} - an upgrade didn't run or didn't finish.");
            if (inspection.Database is { } db)
            {
                if (db.State is { } state && state != "ONLINE") Add(Projects.Health.Bad, "Database", $"The database is {state}.");
                if (db.ReadOnly == true) Add(Projects.Health.Bad, "Database", "The database is read-only.");
                if (db.CompatibilityLevel is < 110 and { } level) Add(Projects.Health.Warning, "Database", $"Compatibility level {level} is unusually old (SQL Server {CompatibilityYear(level) ?? "2008"}).");
                if (inspection.Server?.Collation is { } sc && db.Collation is { } dc && !sc.Equals(dc, StringComparison.OrdinalIgnoreCase))
                    Add(Projects.Health.Warning, "Database", $"The database's collation {dc} differs from the server's {sc} - queries joining temporary tables can fail (\"Cannot resolve the collation conflict\").");
                if (db.AutoClose == true) Add(Projects.Health.Warning, "Database", "AUTO_CLOSE is on - the database closes after each use and opens slowly again.");
                if (db.AutoShrink == true) Add(Projects.Health.Warning, "Database", "AUTO_SHRINK is on - it fragments the database.");
                var data = db.Files.Where(f => f.Type == "ROWS").Sum(f => f.SizeMb);
                foreach (var log in db.Files.Where(f => f.Type == "LOG" && f.SizeMb > 1024 && f.SizeMb > 2 * data))
                    Add(Projects.Health.Warning, "Database", $"The transaction log {log.Name} is {log.SizeMb:N0} MB - over twice the data ({data:N0} MB){(db.RecoveryModel == "FULL" ? ", in FULL recovery without log backups?" : ".")}");
                foreach (var file in db.Files.Where(f => f.UsedMb is { } used && f.SizeMb > 0 && used / f.SizeMb > 0.9 && f.Growth == "none"))
                    Add(Projects.Health.Bad, "Database", $"File {file.Name} is {100 * file.UsedMb!.Value / file.SizeMb:N0}% full and can't grow.");
            }
        }

        // Folders
        foreach (var f in s.Folders)
        {
            if (!f.Exists && f.Required) Add(Projects.Health.Bad, "Filesystem", $"{f.Folder} is missing.");
            else if (f.Exists && f.NeedsWrite && f.Writable == false)
                Add(Projects.Health.Bad, "Filesystem", $"{s.Iis?.Pool?.Account ?? "The app pool"} can't write to {f.Folder} ({f.Via}).");
            else if (f.Exists && f.ReadOnly) Add(Projects.Health.Warning, "Filesystem", $"{f.Folder} is read-only.");
        }

        // Not the assemblies' problems: Assemblies on Advanced lists them, and Project health counts them.
        return list.OrderBy(i => i.Health == Projects.Health.Bad ? 0 : 1).ToList();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<string> AliasesWithoutBinding(DnnPortalDetails portal, IisSiteDetails? site)
    {
        if (site is null) yield break;
        foreach (var alias in portal.OtherAliases.Select(o => o.Alias).Prepend(portal.PrimaryAlias).OfType<string>())
        {
            var host = AliasHost(alias);
            var port = alias.Split('/')[0].Split(':') is [_, var p] && int.TryParse(p, out var n) ? n : (int?)null;
            var served = site.Bindings.Any(b => b.Protocol is "http" or "https" &&
                                                (b.Host.Length == 0 || b.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) &&
                                                (port is null ? b.Port is 80 or 443 : b.Port == port));
            if (!served) yield return alias;
        }
    }

    /// <summary>"dzp.dnndev.me:8080/child" - "dzp.dnndev.me".</summary>
    private static string AliasHost(string alias) => alias.Split('/')[0].Split(':')[0];

    /// <summary>The alias as an address: https when the site has an https binding for its host.</summary>
    private static string AliasUrl(string alias, IisSiteDetails? site)
    {
        var host = AliasHost(alias);
        var https = site?.Bindings.Any(b => b.Protocol == "https" && (b.Host.Length == 0 || b.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) == true;
        return $"{(https ? "https" : "http")}://{alias}";
    }

    private static string ProjectType(ProjectSnapshot s)
    {
        if (s.Row.IsDnn) return s.Config.HasDnnSection ? "DNN site" : "DNN files without DNN's web.config";
        return File.Exists(s.Config.Path) ? "ASP.NET site (no DNN)" : "static site (no web.config)";
    }

    private static string DatabaseNote(ProjectSnapshot s) =>
        s.Connection is null ? $"The database isn't known: {s.Row.Project.DatabaseProblem ?? "web.config names none"}."
        : s.Row.Project.DatabaseIsFile ? "The database is a LocalDB file the site runs itself - not read while the site runs."
        : !s.DatabaseRead ? "Reading the database…"
        : s.Database?.Problem is { } problem ? $"The database couldn't be read: {problem}"
        : "The database has no DNN tables.";

    private static string Authentication(DatabaseConnection c) =>
        c.UsesWindowsAuthentication ? "Windows - the site signs in as its app pool's account" : $"SQL Server login '{c.User}'";

    public static bool SameVersion(string? files, string? database) =>
        files is null || database is null ||
        (Version.TryParse(files, out var a) && Version.TryParse(database, out var b)
            ? a.Major == b.Major && a.Minor == b.Minor && Math.Max(a.Build, 0) == Math.Max(b.Build, 0)
            : files == database);

    private static string? SqlYear(int? major) => major switch
    {
        17 => "2025", 16 => "2022", 15 => "2019", 14 => "2017", 13 => "2016", 12 => "2014", 11 => "2012", 10 => "2008", _ => null
    };

    private static string? CompatibilityYear(int level) => level switch
    {
        170 => "2025", 160 => "2022", 150 => "2019", 140 => "2017", 130 => "2016", 120 => "2014", 110 => "2012", 100 => "2008", _ => null
    };

    /// <summary>A binding redirect or codeBase to a file that isn't there: that assembly can't load. The rest may not matter.</summary>
    private static Health AssemblyHealth(AssemblyProblem problem) =>
        problem.Kind is "redirect" or "codebase" ? Projects.Health.Bad : Projects.Health.Warning;

    /// <summary>"1 problem, 3 warnings" - and the worst of them; "none" and Ok when there are none.</summary>
    private static (string Text, Health Health) Tally(IEnumerable<Health> healths)
    {
        var list = healths.ToList();
        var bad = list.Count(h => h == Projects.Health.Bad);
        var warn = list.Count(h => h == Projects.Health.Warning);
        if (bad == 0 && warn == 0) return ("none", Projects.Health.Ok);
        return (string.Join(", ", new[] { bad > 0 ? Count(bad, "problem") : null, warn > 0 ? Count(warn, "warning") : null }.OfType<string>()),
            bad > 0 ? Projects.Health.Bad : Projects.Health.Warning);
    }

    private static string KindText(string kind) => kind switch
    {
        "duplicate" => "Duplicate",
        "redirect" => "Binding redirect",
        "codebase" => "Code base",
        "version" => "Version conflict",
        "missing" => "Missing",
        _ => kind
    };

    private static string OnOff(bool? value) => value switch { true => "on", false => "off", null => "-" };

    private static string Count(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

    private static string Span(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{(int)span.TotalSeconds} s";

    /// <summary>Reads <see cref="FolderFacts"/> - file-system work, so off the UI thread, once per read of the site.</summary>
    public static FolderFacts ReadFolderFacts(DnnProject project) =>
        new(GitBranch(project.ProjectDirectory), Solutions(project.ProjectDirectory), ProjectBackups.List(project));

    private static IReadOnlyList<string> Solutions(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx"))
                .Select(f => System.IO.Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>The checked-out branch from <c>.git\HEAD</c>; null when the folder isn't a git repository.</summary>
    private static string? GitBranch(string dir)
    {
        var head = System.IO.Path.Combine(dir, ".git", "HEAD");
        if (!File.Exists(head)) return Directory.Exists(System.IO.Path.Combine(dir, ".git")) ? "(git repository)" : null;
        try
        {
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..]
                : text.Length >= 7 ? $"(detached at {text[..7]})" : "(unknown)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(unreadable)";
        }
    }
}
