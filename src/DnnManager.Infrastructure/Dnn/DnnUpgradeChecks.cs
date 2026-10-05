using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// The checks after each upgrade step (see <see cref="IDnnUpgradeChecks"/>): the database read directly, the site through
/// HTTP as a visitor and as the accounts given, DNN's logs from its folder, and Windows' event logs.
/// </summary>
public sealed class DnnUpgradeChecks(IDnnSiteInspector inspector, ILogger<DnnUpgradeChecks> log) : IDnnUpgradeChecks
{
    /// <summary>Pages sampled per portal besides its home page.</summary>
    private const int PagesPerPortal = 6;

    /// <summary>What DNN puts in a page when a module on it failed to load.</summary>
    private static readonly string[] ModuleErrors = ["is currently unavailable", "A critical error has occurred", "An error has occurred."];

    private readonly IDnnSiteInspector _inspector = inspector;
    private readonly ILogger<DnnUpgradeChecks> _log = log;

    public async Task<DnnSiteState> CaptureAsync(DnnSiteAddress site, DatabaseConnection database, CancellationToken ct)
    {
        var counts = await _inspector.CountAsync(site.Directory, database, ct);
        var (extensions, pages) = await ReadSiteAsync(site, database, ct);
        var states = new List<DnnPageState>();
        foreach (var (portal, alias, path) in pages) states.Add(new DnnPageState(portal, $"{alias}/{path}", await ProblemAsync(alias, site.Port, path)));
        return new DnnSiteState(counts, extensions, states, await ItemsAsync(database, ct));
    }

    public async Task<IReadOnlyList<UpgradeFinding>> ValidateAsync(DnnSiteAddress site, DatabaseConnection database, DnnSiteState before, Version expected,
        DateTime sinceUtc, IReadOnlyList<DnnTestAccount> accounts, string appPool, IProgressReporter reporter, CancellationToken ct)
    {
        var found = new List<UpgradeFinding>();
        void Add(UpgradeFinding finding)
        {
            found.Add(finding);
            var text = finding.Detail is null ? $"{finding.Area}: {finding.Title}" : $"{finding.Area}: {finding.Title} - {finding.Detail}";
            switch (finding.Severity)
            {
                case UpgradeFindingSeverity.Blocking: reporter.Fail(text); break;
                case UpgradeFindingSeverity.Warning: reporter.Warn(text); break;
                case UpgradeFindingSeverity.Compatible: reporter.Success(text); break;
                default: reporter.Info(text); break;
            }
        }

        // The database: at the version of the files and of the step, with nothing lost.
        var expectedName = $"{expected.Major}.{expected.Minor}.{Math.Max(expected.Build, 0)}";
        var files = DnnInstall.Version(site.Directory);
        string? dbVersion = null;
        string? dbProblem = null;
        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 20));
            await conn.OpenAsync(ct);
            if (await DnnTables.QualifierAsync(conn, "Version", ct) is { } q)
            {
                await using var cmd = new SqlCommand($"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM {DnnTables.Name(q, "Version")} ORDER BY Major DESC, Minor DESC, Build DESC", conn);
                dbVersion = await cmd.ExecuteScalarAsync(ct) as string;
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            dbProblem = ex.Message.Split('\n')[0].Trim();
        }
        Add(dbProblem is not null
            ? new(UpgradeFindingSeverity.Blocking, "Database", "The database can't be read", dbProblem)
            : dbVersion == expectedName && files == expectedName
                ? new(UpgradeFindingSeverity.Compatible, "Version", $"Files and database at DNN {expectedName}")
                : new(UpgradeFindingSeverity.Blocking, "Version", $"Expected DNN {expectedName} - the files are at {files ?? "?"}, the database at {dbVersion ?? "?"}",
                    "DNN's upgrade didn't run, or didn't finish."));

        var counts = await _inspector.CountAsync(site.Directory, database, ct);
        var (extensions, pages) = await ReadSiteAsync(site, database, ct);
        var items = await ItemsAsync(database, ct);
        var compared = DnnContentComparison.Compare(before, items, extensions);
        var lostCounts = counts is not null && before.Counts is not null ? counts.Lost(before.Counts) : [];
        if (counts is null || before.Counts is null || items.Count == 0)
            Add(new(UpgradeFindingSeverity.Unknown, "Content", "The site's content couldn't be read"));
        else if (lostCounts.Count > 0 || compared.Lost.Count > 0 || compared.FewerPermissions.Count > 0)
            Add(new(UpgradeFindingSeverity.Blocking, "Content", "Content was lost", string.Join(", ",
                lostCounts.Concat(compared.Lost.Take(10).Select(i => $"{i.Kind} {i.Name}")).Concat(compared.FewerPermissions.Take(10).Select(p => $"fewer permissions on {p}")))));
        else
            Add(new(UpgradeFindingSeverity.Compatible, "Content", $"Nothing lost: {counts}"));
        if (compared.RemovedWithExtension.Count > 0)
            Add(new(UpgradeFindingSeverity.Warning, "Content", $"DNN's upgrade removed {compared.RemovedWithExtension.Count} page(s) / module(s) of its own (its Admin and Host pages, or with their extensions)",
                string.Join(", ", compared.RemovedWithExtension.Take(10).Select(i => $"{i.Kind} {i.Name}{(i.Package is { } p ? $" ({p})" : "")}"))));
        if (compared.JobsGone.Count > 0)
            Add(new(UpgradeFindingSeverity.Warning, "Scheduler", $"{compared.JobsGone.Count} scheduled job(s) are gone", string.Join(", ", compared.JobsGone.Select(j => j.Name))));
        var gone = before.Extensions.Except(extensions, StringComparer.OrdinalIgnoreCase).ToList();
        var added = extensions.Except(before.Extensions, StringComparer.OrdinalIgnoreCase).ToList();
        if (gone.Count > 0)
            Add(new(UpgradeFindingSeverity.Warning, "Extensions", $"{gone.Count} extension(s) are gone: {string.Join(", ", gone.Take(15))}",
                "DNN's upgrade removes some of its own (DNN 10: the Telerik ones) - check none of them was one you use."));
        if (added.Count > 0)
            Add(new(UpgradeFindingSeverity.Compatible, "Extensions", $"{added.Count} extension(s) added by the upgrade: {string.Join(", ", added.Take(15))}"));

        // The pages: every portal's home page and a sample of pages - a page that worked before must still work.
        var broken = 0;
        foreach (var (portal, alias, path) in pages)
        {
            var url = $"{alias}/{path}";
            var problem = await ProblemAsync(alias, site.Port, path);
            if (problem is null) continue;
            var wasFine = before.Pages.FirstOrDefault(p => p.Url == url) is { Problem: null };
            broken++;
            Add(new(wasFine ? UpgradeFindingSeverity.Blocking : UpgradeFindingSeverity.Warning, "Pages", $"{url}: {problem}",
                wasFine ? $"It worked before the step ({portal})." : $"It didn't work before the step either ({portal})."));
        }
        if (broken == 0) Add(new(UpgradeFindingSeverity.Compatible, "Pages", $"{pages.Count} page(s) of {pages.Select(p => p.Portal).Distinct().Count()} portal(s) answer without errors"));

        // Signing in - the host also sees the Persona Bar.
        foreach (var account in accounts)
        {
            using var session = new DnnHttpSession(site.Alias, site.Port, TimeSpan.FromMinutes(2));
            DnnHttpSession.SignIn signIn;
            try { signIn = await session.SignInAsync(account.UserName, account.Password); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { signIn = new(false, false, ex.Message); }
            var who = account.IsHost ? $"The host '{account.UserName}'" : $"'{account.UserName}'";
            if (!signIn.SignedIn) Add(new(UpgradeFindingSeverity.Blocking, "Sign-in", $"{who} can't sign in", signIn.Detail));
            // The Persona Bar came with DNN 9: DNN 8 and older show their control bar.
            else if (account.IsHost && expected.Major >= 9 && !signIn.PersonaBar)
                Add(new(UpgradeFindingSeverity.Blocking, "Persona Bar", $"{who} signs in, but the Persona Bar doesn't show"));
            else Add(new(UpgradeFindingSeverity.Compatible, "Sign-in", account.IsHost && expected.Major >= 9 ? $"{who} signs in and sees the Persona Bar" : $"{who} signs in"));
        }

        // What DNN and Windows logged meanwhile.
        foreach (var line in DnnLogErrors(site.Directory, sinceUtc).Take(8))
            Add(new(UpgradeFindingSeverity.Warning, "DNN log", line));
        foreach (var entry in Events(appPool, site.Directory, sinceUtc).Where(e => e.Level <= 2).Take(5))
            Add(new(UpgradeFindingSeverity.Warning, "Event log", $"{entry.Time:HH:mm:ss} {entry.Source}: {entry.Message.Split('\n')[0].Trim()}"));
        return found;
    }

    public async Task<IReadOnlyList<string>> CollectDiagnosticsAsync(string siteDirectory, string appPool, DateTime sinceUtc, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var lines = new List<string>();
        // DNN's logs, and its database scripts' (an empty one ran cleanly).
        foreach (var (relative, pattern) in new[] { (@"Portals\_default\Logs", "*.resources"), (@"Providers\DataProviders\SqlDataProvider", "*.log.resources"), ("App_Data", "*.log*") })
        {
            var source = Path.Combine(siteDirectory, relative);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, pattern))
            {
                if (File.GetLastWriteTimeUtc(file) < sinceUtc.AddMinutes(-1)) continue;
                var target = Path.Combine(folder, relative.Replace('\\', '_') + "_" + Path.GetFileName(file));
                try
                {
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    await using (var output = File.Create(target))
                        await input.CopyToAsync(output, ct);
                    lines.Add($"Kept {relative}\\{Path.GetFileName(file)}");
                    // A database script's log holds only what went wrong - its first lines name the SQL and the error.
                    if (relative.EndsWith("SqlDataProvider", StringComparison.Ordinal))
                        lines.AddRange(ReadShared(target).Select(l => l.Trim().Trim('\uFEFF')).Where(l => l.Length > 0).Take(3)
                            .Select(l => $"Database script {Path.GetFileName(file)} failed: {Short(l)}"));
                }
                catch (IOException ex) { lines.Add($"Couldn't keep {relative}\\{Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        lines.AddRange(DnnLogErrors(siteDirectory, sinceUtc).Where(l => !l.StartsWith("Database script", StringComparison.Ordinal)).Take(10).Select(l => "DNN log: " + l));
        var events = Events(appPool, siteDirectory, sinceUtc).ToList();
        if (events.Count > 0)
        {
            var text = new StringBuilder();
            foreach (var e in events) text.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.LevelName}  {e.Source} ({e.Id})").AppendLine(e.Message).AppendLine();
            await File.WriteAllTextAsync(Path.Combine(folder, "windows-events.txt"), text.ToString(), ct);
            lines.Add($"Kept {events.Count} Windows event(s) about the site and its app pool (windows-events.txt)");
            lines.AddRange(events.Where(e => e.Level <= 2).Take(5).Select(e => $"Event log: {e.Source}: {e.Message.Split('\n')[0].Trim()}"));
        }
        return lines;
    }

    // ─── The site's portals and pages ────────────────────────────────────

    /// <summary>The site's extensions (by name) and, per portal, its home page and a sample of its pages - on this site's binding.</summary>
    private async Task<(IReadOnlyList<string> Extensions, IReadOnlyList<(string Portal, string Alias, string Path)> Pages)> ReadSiteAsync(
        DnnSiteAddress site, DatabaseConnection database, CancellationToken ct)
    {
        var extensions = new List<string>();
        var pages = new List<(string, string, string)>();
        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 20));
            await conn.OpenAsync(ct);
            if (await DnnTables.QualifierAsync(conn, "Version", ct) is not { } q) return (extensions, pages);
            await using (var cmd = new SqlCommand($"SELECT Name FROM {DnnTables.Name(q, "Packages")}", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) extensions.Add(reader.GetString(0));

            // The site's own host name and port; a portal alias on another binding isn't reachable this way.
            var host = site.Alias.Split(':')[0];
            var aliases = new List<(int Portal, string Alias)>();
            await using (var cmd = new SqlCommand($"SELECT PortalID, HTTPAlias FROM {DnnTables.Name(q, "PortalAlias")} ORDER BY PortalID, IsPrimary DESC, PortalAliasID", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) aliases.Add((reader.GetInt32(0), reader.GetString(1)));
            foreach (var portal in aliases.GroupBy(a => a.Portal))
            {
                var alias = portal.Select(a => a.Alias).FirstOrDefault(a => OnBinding(a, host, site.Port));
                if (alias is null) continue;
                var name = $"portal {portal.Key}";
                pages.Add((name, alias.TrimEnd('/'), ""));
                await using var tabs = new SqlCommand(
                    $"SELECT TOP {PagesPerPortal} TabID FROM {DnnTables.Name(q, "Tabs")} WHERE PortalID = @p AND IsDeleted = 0 AND IsVisible = 1 " +
                    "AND DisableLink = 0 AND ISNULL(Url, '') = '' AND TabPath NOT LIKE '//Admin%' AND TabPath NOT LIKE '//Host%' ORDER BY Level, TabOrder", conn);
                tabs.Parameters.AddWithValue("@p", portal.Key);
                await using var reader = await tabs.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) pages.Add((name, alias.TrimEnd('/'), $"Default.aspx?TabId={reader.GetInt32(0)}"));
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log.LogWarning("Reading the portals for the upgrade's checks failed: {Error}", ex.Message);
        }
        return (extensions, pages);
    }

    /// <summary>
    /// Every page, module and scheduled job, with its permissions - what the checks compare one by one. Empty when the
    /// database can't be read.
    /// </summary>
    private async Task<IReadOnlyList<DnnContentItem>> ItemsAsync(DatabaseConnection database, CancellationToken ct)
    {
        var items = new List<DnnContentItem>();
        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.ForApp(database, timeoutSeconds: 30));
            await conn.OpenAsync(ct);
            if (await DnnTables.QualifierAsync(conn, "Version", ct) is not { } q) return items;
            string T(string table) => DnnTables.Name(q, table);
            async Task Read(string sql, Func<SqlDataReader, DnnContentItem> item)
            {
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(item(reader));
            }
            await Read($"SELECT t.TabID, t.TabPath, (SELECT COUNT(*) FROM {T("TabPermission")} p WHERE p.TabID = t.TabID) FROM {T("Tabs")} t WHERE t.IsDeleted = 0",
                r => new DnnContentItem("page", r.GetInt32(0), r.IsDBNull(1) ? $"#{r.GetInt32(0)}" : r.GetString(1), Permissions: r.GetInt32(2)));
            // A module's extension: its package - or, for DNN's own modules that have none (DNN 8's 'Navigation'), its
            // module type, listed as a "type" item of its own so the comparison sees it gone with the upgrade.
            await Read($"SELECT m.ModuleID, ISNULL(MIN(tm.ModuleTitle), ''), MIN(tm.TabID), ISNULL(MIN(pk.Name), MIN(dm.ModuleName)), " +
                       $"(SELECT COUNT(*) FROM {T("ModulePermission")} mp WHERE mp.ModuleID = m.ModuleID) " +
                       $"FROM {T("Modules")} m JOIN {T("ModuleDefinitions")} md ON md.ModuleDefID = m.ModuleDefID " +
                       $"JOIN {T("DesktopModules")} dm ON dm.DesktopModuleID = md.DesktopModuleID LEFT JOIN {T("Packages")} pk ON pk.PackageID = dm.PackageID " +
                       $"LEFT JOIN {T("TabModules")} tm ON tm.ModuleID = m.ModuleID AND tm.IsDeleted = 0 WHERE m.IsDeleted = 0 GROUP BY m.ModuleID",
                r => new DnnContentItem("module", r.GetInt32(0), $"'{r.GetString(1)}' (#{r.GetInt32(0)})", r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt32(4)));
            await Read($"SELECT DesktopModuleID, ModuleName FROM {T("DesktopModules")}",
                r => new DnnContentItem("type", r.GetInt32(0), r.GetString(1)));
            await Read($"SELECT ScheduleID, TypeFullName FROM {T("Schedule")}",
                r => new DnnContentItem("job", r.GetInt32(0), r.IsDBNull(1) ? $"#{r.GetInt32(0)}" : r.GetString(1)));
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log.LogWarning("Reading the site's pages and modules for the upgrade's checks failed: {Error}", ex.Message);
            items.Clear();
        }
        return items;
    }

    /// <summary>Whether a portal alias (<c>host[:port][/child]</c>) is served by this site's binding.</summary>
    private static bool OnBinding(string alias, string host, int port)
    {
        var hostPort = alias.Split('/')[0];
        var parts = hostPort.Split(':');
        var aliasPort = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 80;
        return parts[0].Equals(host, StringComparison.OrdinalIgnoreCase) && aliasPort == port;
    }

    /// <summary>What is wrong with a page - null when it answers (or asks to sign in) without a module error.</summary>
    private static async Task<string?> ProblemAsync(string alias, int port, string path)
    {
        using var session = new DnnHttpSession(alias, port, TimeSpan.FromMinutes(3));
        try
        {
            var page = await session.GetAsync(path);
            if (page.AtInstaller) return "it sends visitors to DNN's installer";
            if (page.AtErrorPage) return "it shows DNN's error page";
            if (page.AtLogin && page.Status == 200) return null;
            if (page.Status != 200) return $"HTTP {page.Status}";
            return ModuleErrors.FirstOrDefault(e => page.Html.Contains(e, StringComparison.OrdinalIgnoreCase)) is { } error ? $"a module error (\"{error}\")" : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ex.Message.Split('\n')[0].Trim();
        }
    }

    // ─── Logs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The [ERROR] and [FATAL] lines DNN logged since <paramref name="sinceUtc"/> - with the line after one when it carries the
    /// message (DNN puts a SQL error's text there) - and its database scripts' problems.
    /// </summary>
    internal static IEnumerable<string> DnnLogErrors(string siteDirectory, DateTime sinceUtc)
    {
        // A second's slack: DNN's log times are to the millisecond, the comparison to the second.
        var since = sinceUtc.ToLocalTime().AddSeconds(-1);
        var logs = Path.Combine(siteDirectory, "Portals", "_default", "Logs");
        if (Directory.Exists(logs))
            foreach (var file in Directory.EnumerateFiles(logs, "*.log.resources"))
            {
                var lines = ReadShared(file);
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (!line.Contains("[ERROR]", StringComparison.Ordinal) && !line.Contains("[FATAL]", StringComparison.Ordinal)) continue;
                    if (DnnInstaller.LogTime(line) is not { } at || at < since) continue;
                    var next = i + 1 < lines.Count ? lines[i + 1].Trim() : "";
                    var message = next.Length > 0 && DnnInstaller.LogTime(next) is null && !next.StartsWith("at ", StringComparison.Ordinal) ? $"{line} → {next}" : line;
                    yield return Short(message);
                }
            }
        var scripts = Path.Combine(siteDirectory, "Providers", "DataProviders", "SqlDataProvider");
        if (Directory.Exists(scripts))
            foreach (var file in Directory.EnumerateFiles(scripts, "*.log.resources"))
                if (File.GetLastWriteTimeUtc(file) >= sinceUtc && ReadShared(file).FirstOrDefault(l => l.Trim().Trim('﻿').Length > 0) is { } first)
                    yield return $"Database script {Path.GetFileName(file)}: {Short(first)}";
    }

    private sealed record EventEntry(DateTime Time, int Level, string LevelName, string Source, int Id, string Message);

    /// <summary>
    /// Windows' events since <paramref name="sinceUtc"/> about the site - ASP.NET's errors and warnings naming its folder,
    /// worker process crashes - and its app pool (IIS's WAS: recycles, rapid-fail shutdowns).
    /// </summary>
    private static IEnumerable<EventEntry> Events(string appPool, string siteDirectory, DateTime sinceUtc)
    {
        var since = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var queries = new[]
        {
            ("Application", $"*[System[Provider[@Name='ASP.NET 4.0.30319.0' or @Name='.NET Runtime' or @Name='Application Error'] and TimeCreated[@SystemTime>='{since}']]]",
                (Func<string, bool>)(m => m.Contains(siteDirectory, StringComparison.OrdinalIgnoreCase) || m.Contains("w3wp.exe", StringComparison.OrdinalIgnoreCase) && m.Contains(appPool, StringComparison.OrdinalIgnoreCase))),
            ("System", $"*[System[Provider[@Name='Microsoft-Windows-WAS'] and TimeCreated[@SystemTime>='{since}']]]",
                m => appPool.Length > 0 && m.Contains($"'{appPool}'", StringComparison.OrdinalIgnoreCase))
        };
        var entries = new List<EventEntry>();
        foreach (var (logName, query, about) in queries)
        {
            try
            {
                using var reader = new EventLogReader(new EventLogQuery(logName, PathType.LogName, query));
                for (var record = reader.ReadEvent(); record is not null && entries.Count < 200; record = reader.ReadEvent())
                    using (record)
                    {
                        string message;
                        try { message = record.FormatDescription() ?? ""; } catch (EventLogException) { message = ""; }
                        if (!about(message)) continue;
                        string level;
                        try { level = record.LevelDisplayName ?? $"Level {record.Level}"; } catch (EventLogException) { level = $"Level {record.Level}"; }
                        entries.Add(new EventEntry(record.TimeCreated ?? DateTime.Now, record.Level ?? 4, level, record.ProviderName, record.Id, message));
                    }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                // The event log can't be read here - the checks go on without it.
            }
        }
        return entries.OrderBy(e => e.Time);
    }

    private static IReadOnlyList<string> ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string Short(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
