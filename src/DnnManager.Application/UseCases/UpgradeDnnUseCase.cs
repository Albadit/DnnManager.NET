using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class UpgradeDnnRequest
{
    /// <summary>The IIS site's name.</summary>
    public required string SiteName { get; init; }

    /// <summary>The folder the site serves.</summary>
    public required string Directory { get; init; }

    /// <summary>The GitHub releases API of the repository the versions come from.</summary>
    public required string ReleaseApiUrl { get; init; }

    /// <summary>The version to end at, e.g. <c>v10.3.3</c> - reached through every step of DNN's upgrade path.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Accounts signed in with after each step (the host sees its Persona Bar) - never kept. The app doesn't ask for
    /// any; the tests that upgrade real sites give the host they installed with.
    /// </summary>
    public IReadOnlyList<DnnTestAccount> Accounts { get; init; } = [];
}

/// <summary>
/// Upgrades a project's DNN as DNN's suggested upgrade path says - one listed version at a time, never straight to the
/// newest - checking it all the way (docs/dnn-upgrades.md):
/// <list type="number">
/// <item><b>Analyse</b> the site, its database, IIS and this PC (<see cref="IDnnSiteInspector"/>) and make the <b>plan</b>:
/// the steps, each with its checks (<see cref="DnnUpgradeAnalyser"/>). Anything blocking - nothing is changed.</item>
/// <item>For each step: a <b>backup</b> of the site's files and database (with the plan, the site's inventory and web.config
/// beside it); the step's <b>files</b> (its upgrade package, or from DNN 10.2 on its install package as DNN's own local
/// upgrade puts it in); DNN's <b>upgrade</b> (<c>Install.aspx?mode=upgrade</c>); a <b>restart</b>; then the <b>checks</b>
/// (<see cref="IDnnUpgradeChecks"/>).</item>
/// <item>A step that fails - or a cancel - stops the chain: what tells why is kept with the stage's backup, the cause and
/// fixes are said (<see cref="DnnUpgradeDiagnosis"/>), and the site is put back from that backup - the last version that
/// worked. The steps before it stay done; their backups go back further.</item>
/// </list>
/// </summary>
public sealed class UpgradeDnnUseCase(
    IIisManager iis,
    IProjectRepository projects,
    LocalSqlContainer sql,
    IDnnReleaseService releases,
    IDnnPackageInstaller packages,
    IDnnInstaller installer,
    IDnnSiteInspector inspector,
    IDnnUpgradeChecks checks,
    IProjectRecords records,
    ExportProjectUseCase export,
    RestoreBackupUseCase restore,
    OperationUndo undo,
    IUserPrompt prompt,
    IOptions<AppOptions> options)
{
    private readonly IIisManager _iis = iis;
    private readonly IProjectRepository _projects = projects;
    private readonly LocalSqlContainer _sql = sql;
    private readonly IDnnReleaseService _releases = releases;
    private readonly IDnnPackageInstaller _packages = packages;
    private readonly IDnnInstaller _installer = installer;
    private readonly IDnnSiteInspector _inspector = inspector;
    private readonly IDnnUpgradeChecks _checks = checks;
    private readonly IProjectRecords _records = records;
    private readonly ExportProjectUseCase _export = export;
    private readonly RestoreBackupUseCase _restore = restore;
    private readonly OperationUndo _undo = undo;
    private readonly IUserPrompt _prompt = prompt;
    private readonly AppOptions _options = options.Value;

    // How long putting a step's backup back may take as an undo step - a whole site and its database (a BACPAC import).
    private static readonly TimeSpan RestoreLimit = TimeSpan.FromMinutes(20);

    /// <summary>What the analyser makes of the site - the plan, before anything changes. The dialog shows it.</summary>
    public async Task<Result<DnnUpgradePlan>> PlanAsync(string siteName, string directory, Version target, CancellationToken ct)
    {
        var project = _projects.Build(siteName, directory);
        var database = _sql.DatabaseOf(project);
        if (await SignInRefusedAsync(database, "Analysing the site for the upgrade", ct) is { } refused) return Result<DnnUpgradePlan>.Fail(refused);
        var facts = await _inspector.InspectAsync(siteName, directory, database, ct);
        return Result<DnnUpgradePlan>.Ok(DnnUpgradeAnalyser.Plan(facts, target));
    }

    // Servers the user agreed to sign in to (see below).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> AgreedServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Null when DNN Manager may sign in to <paramref name="database"/> - the one the site's web.config names - else why
    /// it doesn't. With Windows authentication to a server that isn't on this PC and isn't the one in Settings → Database
    /// server, the user is asked first: the site's app pool can change web.config, and that server would get their sign-in.
    /// </summary>
    private async Task<string?> SignInRefusedAsync(DatabaseConnection? database, string why, CancellationToken ct)
    {
        if (database is not { Kind: not DatabaseKind.LocalDbFile, Authentication: SqlAuthentication.Windows } ||
            SqlServerAddress.MaySignInAsUser(database.Server, _options.DatabaseServer) || AgreedServers.ContainsKey(database.Server))
            return null;
        // A yes holds until DNN Manager closes: the upgrade dialog plans again at every version picked.
        if (await _prompt.ConfirmAsync(SqlServerAddress.SignInQuestion(database.Server, why),
                                       $"Sign in to {database.Server}", "Don't sign in", false, ct))
        {
            AgreedServers[database.Server] = true;
            return null;
        }
        return $"Not signed in to {database.Server} with your Windows account - the upgrade reads and backs up its database there. Nothing was changed.";
    }

    public async Task<Result> ExecuteAsync(UpgradeDnnRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        // ─── Analyse and plan - nothing changes until it has passed ───
        if (DnnInstall.Number(req.Version) is not { } target) return Result.Fail($"'{req.Version}' isn't a DNN version.");
        var project = _projects.Build(req.SiteName, req.Directory);
        if (_iis.GetSiteDetails(req.SiteName) is not { } site) return Result.Fail($"IIS has no site named '{req.SiteName}'.");
        var database = _sql.DatabaseOf(project);
        if (await SignInRefusedAsync(database, "Upgrading DNN", ct) is { } refused) return Result.Fail(refused);
        var facts = await _inspector.InspectAsync(req.SiteName, req.Directory, database, ct);
        var plan = DnnUpgradeAnalyser.Plan(facts, target);
        reporter.Plan(["Analyse", .. plan.Steps.Select(s => DnnUpgradeStep.Name(s.Step.To))]);
        reporter.Context($"DNN {DnnUpgradeStep.Name(plan.Current)} → {DnnUpgradeStep.Name(plan.Target)} in {plan.Steps.Count} step(s)");
        reporter.Step("Analysing the site and planning the upgrade", "Analyse");
        foreach (var line in plan.Describe().Split(Environment.NewLine)) if (line.Length > 0) reporter.Info(line);
        if (!plan.CanStart)
        {
            foreach (var blocking in plan.Blocking) reporter.Fail($"{blocking.Area}: {blocking.Title}" + (blocking.Fix is null ? "" : $" → {blocking.Fix}"));
            return Result.Fail($"The upgrade didn't start - nothing was changed: {string.Join(" ", plan.Blocking.Select(b => b.Title + "."))}");
        }
        if (AddressOf(site, req.Directory) is not { } address || database is null)
            return Result.Fail("The site has no http binding with a host name, or no database - nothing was changed.");
        // Every step's release first: a chain that can't be finished isn't started.
        var stepReleases = new List<DnnRelease>();
        // One list from GitHub for all the steps (its API allows 60 requests an hour) - a release it doesn't hold is asked for on its own.
        var listed = await _releases.ListReleasesAsync(req.ReleaseApiUrl, ct);
        foreach (var planned in plan.Steps)
        {
            var known = listed.Value?.Releases.FirstOrDefault(r => DnnInstall.Number(r.Version) is { } v && DnnUpgradePath.Normalize(v) == planned.Step.To);
            var found = known is not null ? Result<DnnRelease>.Ok(known) : await _releases.GetReleaseAsync(req.ReleaseApiUrl, Short(planned.Step.To), ct);
            if (!found.Success) return Result.Fail($"DNN {Short(planned.Step.To)}, a step of the upgrade path, wasn't found: {found.Error} Nothing was changed.");
            stepReleases.Add(found.Value!);
        }
        reporter.Success($"The plan holds: {plan.Steps.Count} step(s), nothing blocking.");

        var done = new List<string>();
        var stageBackups = new List<(string To, string Folder)>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i].Step;
            var result = await RunStepAsync(req, project, site, address, database, plan, step, stepReleases[i], i + 1, reporter, ct);
            if (result.Folder is not null) stageBackups.Add((Short(step.From), result.Folder));
            if (!result.Passed)
            {
                var reached = !result.PutBack
                    ? $"The site may be half-upgraded: put it back with Restore backup ({result.Folder ?? "the step's backup"})."
                    : done.Count == 0 ? $"The site is at DNN {Short(step.From)} again, as it was."
                    : $"The site is at DNN {Short(step.From)} - the steps before it ({string.Join(", ", done)}) stay done.";
                reporter.Info($"Backups to go back further: {string.Join("; ", stageBackups.Select(b => $"DNN {b.To}: {b.Folder}"))}");
                return Result.Fail($"Step {i + 1} ({step}) failed: {result.Error} {reached}");
            }
            done.Add($"{step}");
        }

        reporter.Step("Upgrade complete");
        reporter.Success($"'{req.SiteName}' runs DNN {Short(plan.Target)} (was {Short(plan.Current)}) - {done.Count} step(s): {string.Join(", ", done)}.");
        foreach (var (from, folder) in stageBackups) reporter.Info($"Backup of DNN {from}: {folder} - Restore backup puts the site back to it.");
        return Result.Ok();
    }

    /// <summary>How a step went; <paramref name="PutBack"/>: the site is as it was before it (it passed, never changed, or was put back).</summary>
    private sealed record StepResult(bool Passed, string? Error, string? Folder, bool PutBack = true);

    /// <summary>One step: backup, files, DNN's upgrade, restart, checks - and on failure the diagnosis and the way back.</summary>
    private async Task<StepResult> RunStepAsync(UpgradeDnnRequest req, DnnProject project, IisSiteDetails site, DnnSiteAddress address,
        DatabaseConnection database, DnnUpgradePlan plan, DnnUpgradeStep step, DnnRelease release, int number, IProgressReporter reporter, CancellationToken ct)
    {
        var stage = DnnUpgradeStep.Name(step.To);
        var appPool = site.Pool?.Name ?? req.SiteName;

        // ─── Backup, and what the site was like ───
        reporter.Step($"Step {number}: {step} - backing up DNN {Short(step.From)}", stage);
        var folder = ProjectBackups.NewFolder(project, DateTime.Now);
        var backup = new RestoreBackupRequest
        {
            SiteName = req.SiteName, Directory = req.Directory,
            SiteZip = Path.Combine(folder, ProjectBackups.SiteZipName(project)),
            Database = Path.Combine(folder, ProjectBackups.DatabaseName(project, ".bacpac")),
            // Asked before the upgrade started (SignInRefusedAsync): putting the backup back doesn't ask again.
            SignInAgreed = true
        };
        var backedUp = await _export.ExecuteAsync(new ExportProjectRequest
        {
            ProjectName = req.SiteName, ProjectDirectory = req.Directory, ZipPath = backup.SiteZip, BacpacPath = backup.Database
        }, reporter, ct);
        if (!backedUp.Success) return new StepResult(false, $"The backup failed, so the step didn't start - nothing was changed: {backedUp.Error}", null);
        // The backup stays whatever comes next: it is this step's way back.
        _undo.Keep();
        var before = await _checks.CaptureAsync(address, database, ct);
        await WriteStageNotesAsync(folder, req, step, plan, before);
        reporter.Success($"Backup of DNN {Short(step.From)}: {folder}");

        // From here on the site changes: a failure or a cancel puts this backup back - and a site that was stopped stays so.
        var wasRunning = IisStates.IsStarted(site.State);
        // A whole site and its database put back: it has longer than an ordinary undo step.
        _undo.Add($"Put '{req.SiteName}' back as DNN {Short(step.From)} (the step's backup)", async _ =>
        {
            var restored = await _restore.ExecuteAsync(backup, reporter, CancellationToken.None);
            if (!wasRunning) _iis.StopSite(req.SiteName);
            return restored;
        }, RestoreLimit);
        var startedUtc = DateTime.UtcNow;
        try
        {
            return await ChangeAndCheckAsync(req, address, database, step, release, number, folder, appPool, wasRunning, before, startedUtc, reporter, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever it was (an address the checks can't read, a folder they may not write): the site is put back.
            return await FailAsync(step, $"Unexpected error: {ex.Message}", folder, req.Directory, appPool, startedUtc, [], reporter);
        }
    }

    /// <summary>The step's changes - the new files, DNN's upgrade, the restart - and its checks; a failure puts the backup back.</summary>
    private async Task<StepResult> ChangeAndCheckAsync(UpgradeDnnRequest req, DnnSiteAddress address, DatabaseConnection database,
        DnnUpgradeStep step, DnnRelease release, int number, string folder, string appPool, bool wasRunning, DnnSiteState before, DateTime startedUtc,
        IProgressReporter reporter, CancellationToken ct)
    {
        // ─── The new files ───
        reporter.Step($"Step {number}: putting in DNN {Short(step.To)}'s files" +
                      (step.Method == DnnUpgradeMethod.LocalUpgrade ? " (DNN's local upgrade, from its install package)" : " (its upgrade package)"));
        var stopped = await StopAndFreeAsync(req, reporter);
        if (!stopped.Success) return await FailAsync(step, $"Could not stop the site for the new files: {stopped.Error}", folder, req.Directory, appPool, startedUtc, [], reporter);
        var files = step.Method == DnnUpgradeMethod.LocalUpgrade
            ? await _packages.ExtractLocalUpgradeAsync(release, req.Directory, reporter, ct)
            : await _packages.ExtractUpgradeAsync(release, req.Directory, reporter, ct);
        var started = _iis.StartSite(req.SiteName);
        if (!files.Success) return await FailAsync(step, files.Error!, folder, req.Directory, appPool, startedUtc, [], reporter);
        if (!started.Success) return await FailAsync(step, $"Could not start the site to upgrade it: {started.Error}", folder, req.Directory, appPool, startedUtc, [], reporter);

        // ─── DNN's upgrade ───
        reporter.Step($"Step {number}: running DNN's upgrade to {Short(step.To)}");
        var output = Path.Combine(folder, $"dnn-upgrade-output-{Short(step.To)}.html");
        var upgraded = await _installer.UpgradeAsync(address, database.Password, reporter, ct, output);
        reporter.Info($"DNN's output, as it answered: {output}");
        if (!upgraded.Success) return await FailAsync(step, upgraded.Error!, folder, req.Directory, appPool, startedUtc, [], reporter);

        // ─── Restart - DNN leaves its lock behind, held open by its worker process, then the checks ───
        reporter.Step($"Step {number}: restarting the site");
        var freed = await StopAndFreeAsync(req, reporter);
        if (!freed.Success) return await FailAsync(step, freed.Error!, folder, req.Directory, appPool, startedUtc, [], reporter);
        var restarted = _iis.StartSite(req.SiteName);
        if (!restarted.Success) return await FailAsync(step, $"The site didn't start again: {restarted.Error}", folder, req.Directory, appPool, startedUtc, [], reporter);
        var warm = await _installer.WarmUpAsync(address, startedUtc, reporter, ct);
        if (!warm.Success) return await FailAsync(step, warm.Error!, folder, req.Directory, appPool, startedUtc, [], reporter);

        reporter.Step($"Step {number}: checking DNN {Short(step.To)}");
        var found = await _checks.ValidateAsync(address, database, before, step.To, startedUtc, req.Accounts, appPool, reporter, ct);
        var blocking = found.Where(f => f.Severity == UpgradeFindingSeverity.Blocking).ToList();
        if (blocking.Count > 0)
            return await FailAsync(step, string.Join(" ", blocking.Select(b => b.Title + (b.Detail is null ? "." : $" ({b.Detail})."))), folder, req.Directory, appPool,
                startedUtc, found.Select(f => f.ToString()), reporter);

        // ─── Passed: DNN's installer pages and its copies of web.config go; this version is the next step's starting point ───
        _installer.CleanUp(req.Directory, installed: true);
        if (!wasRunning) _iis.StopSite(req.SiteName);
        if (_records.Find(req.SiteName) is { } record) _records.Save(record with { DnnVersion = Short(step.To) });
        _undo.Keep();
        await File.AppendAllTextAsync(Path.Combine(folder, ProjectBackups.NoteFile),
            $"{Environment.NewLine}Result: passed - the site ran DNN {Short(step.To)} after this step ({DateTime.Now:yyyy-MM-dd HH:mm:ss}).{Environment.NewLine}", CancellationToken.None);
        reporter.Success($"Step {number} passed: '{req.SiteName}' runs DNN {Short(step.To)}.");
        return new StepResult(true, null, folder);
    }

    /// <summary>
    /// A step failed: what tells why is kept with the stage's backup, the cause and fixes are said, and the site is put back
    /// from the backup - the version that worked before the step.
    /// </summary>
    private async Task<StepResult> FailAsync(DnnUpgradeStep step, string error, string folder, string directory, string appPool, DateTime sinceUtc,
        IEnumerable<string> evidence, IProgressReporter reporter)
    {
        reporter.Fail(error);
        var kept = Path.Combine(folder, "failed-upgrade");
        var diagnosis = DnnUpgradeDiagnosis.Explain(error, evidence);
        try
        {
            // What tells why is a help, not a must: whatever goes wrong collecting it, the site is still put back below.
            var diagnostics = (await _checks.CollectDiagnosticsAsync(directory, appPool, sinceUtc, kept, CancellationToken.None)).ToList();
            // Where DNN's own output ended - its last lines say what it was doing when it stopped.
            var output = Path.Combine(folder, $"dnn-upgrade-output-{Short(step.To)}.html");
            if (File.Exists(output)) diagnostics.AddRange(LastLines(await File.ReadAllTextAsync(output, CancellationToken.None), 8).Select(l => $"DNN's output: {l}"));
            foreach (var line in diagnostics) reporter.Info(line);
            diagnosis = DnnUpgradeDiagnosis.Explain(error, diagnostics.Concat(evidence));
            reporter.Warn($"Likely cause: {diagnosis.Cause}");
            foreach (var fix in diagnosis.Fixes) reporter.Info($"→ {fix}");
            Directory.CreateDirectory(kept);
            await File.WriteAllTextAsync(Path.Combine(kept, "what-happened.txt"),
                $"Step {step} failed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.{Environment.NewLine}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}" +
                $"Likely cause: {diagnosis.Cause}{Environment.NewLine}{string.Concat(diagnosis.Fixes.Select(f => $"- {f}{Environment.NewLine}"))}{Environment.NewLine}" +
                string.Join(Environment.NewLine, diagnostics.Concat(evidence)), CancellationToken.None);
            reporter.Info($"Kept to see why: {kept}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reporter.Warn($"Could not keep what tells why the step failed: {ex.Message}");
        }

        reporter.Step($"Putting the site back as DNN {Short(step.From)} (the step's backup)");
        var back = await _undo.RunAsync(reporter);
        return new StepResult(false, back
            ? $"{error} Likely cause: {diagnosis.Cause}"
            : $"{error} Likely cause: {diagnosis.Cause} Putting the backup back didn't fully work - see the Output tab.", folder, back);
    }

    /// <summary>The stage's notes beside its backup: why it was made, the plan, and the site's inventory as it was.</summary>
    private static async Task WriteStageNotesAsync(string folder, UpgradeDnnRequest req, DnnUpgradeStep step, DnnUpgradePlan plan, DnnSiteState before)
    {
        Directory.CreateDirectory(folder);
        var notes = new StringBuilder()
            .AppendLine($"Before upgrading DNN {step} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Site: {req.SiteName} ({req.Directory}), DNN {Short(step.From)}")
            .AppendLine($"Content: {before.Counts?.ToString() ?? "couldn't be counted"} ({before.Items.Count(i => i.Kind != "type")} pages, modules and scheduled jobs recorded to compare with)")
            .AppendLine()
            .AppendLine($"Extensions ({before.Extensions.Count}):")
            .AppendLine(string.Join(Environment.NewLine, before.Extensions.Order(StringComparer.OrdinalIgnoreCase).Select(e => "  " + e)))
            .AppendLine()
            .AppendLine("Pages checked before the step:")
            .AppendLine(string.Join(Environment.NewLine, before.Pages.Select(p => $"  {p.Url}: {p.Problem ?? "fine"}")))
            .AppendLine()
            .AppendLine("The plan:")
            .AppendLine(plan.Describe());
        await File.WriteAllTextAsync(Path.Combine(folder, ProjectBackups.NoteFile), notes.ToString(), CancellationToken.None);
        // web.config on its own too - the zip has it, but this is the one to compare with.
        var webConfig = Path.Combine(req.Directory, "web.config");
        if (File.Exists(webConfig)) File.Copy(webConfig, Path.Combine(folder, "web.config.before"), overwrite: true);
    }

    /// <summary>How long the site's worker process gets to end before it is ended by force.</summary>
    private static readonly TimeSpan WorkerExit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The site stopped and its worker process gone, and the installBlocker.lock DNN left deleted. DNN opens the lock
    /// without closing it and, at the end of its upgrade, tries for a minute to delete it - which it can't while it holds it
    /// open itself (tested on IIS: an IOException in its log). Only the worker process ending frees it; a lock left makes
    /// DNN answer every request with "the site was accessed while an installation/upgrade was in progress".
    /// </summary>
    private async Task<Result> StopAndFreeAsync(UpgradeDnnRequest req, IProgressReporter reporter)
    {
        reporter.Progress($"Stopping '{req.SiteName}' and waiting for its worker process to end…");
        var stopped = _iis.StopSiteAndWait(req.SiteName, WorkerExit);
        if (!stopped.Success) return stopped;
        var lockFile = Path.Combine(req.Directory, "installBlocker.lock");
        for (var attempt = 1; File.Exists(lockFile); attempt++)
        {
            try
            {
                File.Delete(lockFile);
                reporter.Info("Removed the installBlocker.lock DNN left behind - while it is there, DNN takes every visit for an upgrade in progress.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                    return Result.Fail($"DNN's installBlocker.lock can't be deleted, even with the site stopped: {ex.Message} " +
                                       "While it is there, DNN takes every visit for an upgrade in progress.");
                await Task.Delay(1000);
            }
        }
        return Result.Ok();
    }

    private static string Short(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    /// <summary>
    /// The last <paramref name="count"/> lines of DNN's own output in its HTML answer - before ASP.NET's error page, if it
    /// broke off into one: that page's head (its title, its styles) comes before its "Server Error in" heading.
    /// </summary>
    internal static IEnumerable<string> LastLines(string html, int count)
    {
        const System.Text.RegularExpressions.RegexOptions options =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline;
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<head\b.*?</head>|<style\b.*?</style>|<script\b.*?</script>|<!--.*?-->", "", options);
        if (text.IndexOf("Server Error in", StringComparison.OrdinalIgnoreCase) is var cut and >= 0) text = text[..cut];
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>|</h\d>|</p>|</div>|</li>", "\n", options);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "", options);
        return System.Net.WebUtility.HtmlDecode(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(count);
    }

    /// <summary>The site on this machine through its first http binding with a host name - as DNN's installer is run.</summary>
    private static DnnSiteAddress? AddressOf(IisSiteDetails site, string directory) =>
        site.Bindings.FirstOrDefault(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port is not null && b.Host.Length > 0)
            is { } binding
            ? new DnnSiteAddress(directory, DnnSiteAddress.AliasFor(binding.Host, binding.Port!.Value), binding.Port.Value)
            : null;
}
