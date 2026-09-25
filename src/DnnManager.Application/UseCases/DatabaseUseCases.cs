using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Shared helpers for resolving a developer project's local database from its configuration, so the
/// backup (export) and restore (import) paths agree on which database the site actually uses.
/// </summary>
internal static class DeveloperDb
{
    /// <summary>
    /// The database the project's site actually connects to: the Initial Catalog of the SiteSqlServer
    /// connection string in the project's web.config. Returns null when web.config has no usable SQL
    /// connection (LocalDB, missing file, or unparseable), so callers fall back to the conventional
    /// {project}_dnndev name. Only the database NAME is taken from web.config - never the server,
    /// because the local export authenticates as the Docker sa and web.config's Data Source may point elsewhere.
    /// </summary>
    public static string? FromWebConfig(DnnProject project, IWebConfigService webConfig)
    {
        var conn = webConfig.ReadSiteSqlServer(Path.Combine(project.ProjectDirectory, "web.config"));
        return conn.Success && conn.Value is not null && !string.IsNullOrWhiteSpace(conn.Value.Database)
            ? conn.Value.Database
            : null;
    }

    /// <summary>Makes a database name safe to embed in a backup file name.</summary>
    public static string SanitizeForFileName(string dbName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(dbName.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(cleaned) ? "db" : cleaned;
    }
}

public sealed class ExportDatabaseUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IWebConfigService _webConfig;
    private readonly IDockerService _docker;
    private readonly IBacpacService _bacpac;
    private readonly ILogger<ExportDatabaseUseCase> _log;

    public ExportDatabaseUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IWebConfigService webConfig,
        IDockerService docker,
        IBacpacService bacpac,
        ILogger<ExportDatabaseUseCase> log)
    {
        _opts = opts.Value; _projects = projects; _webConfig = webConfig; _docker = docker;
        _bacpac = bacpac; _log = log;
    }

    public async Task<Result> ExecuteAsync(string projectName, DnnEnvironment env, IProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            var p = _projects.Build(projectName);

            if (env != DnnEnvironment.Developer)
            {
                reporter.Info("Production export is delegated to the remote SQL Server.");
                reporter.Info("Edit/extend the use case to call your remote BACKUP DATABASE flow.");
                return Result.Fail("Production export not implemented in this build.");
            }

            // Back up whatever database the project's site actually connects to: take the database
            // NAME from the SiteSqlServer connection string in the project's web.config (falling back
            // to the conventional {project}_dnndev name). The SERVER is always the local Docker SQL
            // Server - we authenticate as its sa, so we must not trust web.config's Data Source, which
            // may be a named instance / alias / stale port the Docker sa can't reach.
            var webDb = DeveloperDb.FromWebConfig(p, _webConfig);
            var dbName = webDb ?? _opts.DatabaseNameFor(p.Name);
            var server = await ResolveLocalServerAsync(ct);
            if (server is null)
                return Result.Fail($"The shared SQL Server container '{_opts.Docker.ContainerName}' isn't running - start it and retry.");
            reporter.Info(webDb is not null
                ? $"Backing up the database the site uses (from web.config SiteSqlServer): [{dbName}]."
                : $"web.config had no usable SiteSqlServer connection; using the default name [{dbName}].");

            var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
            if (!ensured.Success) return ensured;

            Directory.CreateDirectory(p.BackupDirectory);
            // Encode the database name in the file name so a backup's contents are identifiable later
            // (the import side picks a file from disk; the source DB isn't otherwise surfaced there).
            var name = $"developer_{DeveloperDb.SanitizeForFileName(dbName)}_{DateTime.Now:yyyyMMdd_HHmmss}.bacpac";
            var dest = Path.Combine(p.BackupDirectory, name);

            reporter.Step($"Backing up [{dbName}] on {server}");
            // Export a BACPAC via SqlPackage, connecting as the local Docker sa for full read rights
            // (regardless of the limited login the site itself uses).
            var source = new SiteSqlConnection(server, dbName, "sa", _opts.Docker.SaPassword);
            var export = await _bacpac.ExportAsync(source, dest, reporter, ct);
            if (!export.Success) return export;

            reporter.Success($"Export complete: {dest}");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export failed");
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The local Docker SQL Server endpoint to connect to as sa (the configured container IP + the
    /// shared container's currently-published port), or null when the container isn't running. We
    /// deliberately do NOT fall back to the default port: with the container down there is no local DB
    /// to back up, and blindly targeting it could hit an unrelated SQL Server listening there.
    /// </summary>
    private async Task<string?> ResolveLocalServerAsync(CancellationToken ct)
    {
        var port = await _docker.GetPublishedPortAsync(_opts.Docker.ContainerName, ct);
        return port is null ? null : _opts.ServerFor(port.Value);
    }
}

public sealed class ImportDatabaseUseCase
{
    private readonly IProjectRepository _projects;
    private readonly IWebConfigService _webConfig;
    private readonly IDockerService _docker;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly IBacpacService _bacpac;
    private readonly IRemoteSqlAdminService _remoteAdmin;
    private readonly IUserPrompt _prompt;
    private readonly AppOptions _opts;
    private readonly ILogger<ImportDatabaseUseCase> _log;

    public ImportDatabaseUseCase(
        IProjectRepository projects, IWebConfigService webConfig, IDockerService docker, LocalSqlContainer sqlContainer,
        IBacpacService bacpac, IRemoteSqlAdminService remoteAdmin, IUserPrompt prompt,
        IOptions<AppOptions> opts, ILogger<ImportDatabaseUseCase> log)
    {
        _projects = projects; _webConfig = webConfig; _docker = docker; _sqlContainer = sqlContainer; _bacpac = bacpac;
        _remoteAdmin = remoteAdmin; _prompt = prompt; _opts = opts.Value; _log = log;
    }

    public async Task<Result> ExecuteAsync(string projectName, DnnEnvironment env, string backupFilePath,
        IProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(backupFilePath)) return Result.Fail($"Backup file not found: {backupFilePath}");

            var p = _projects.Build(projectName);
            return env == DnnEnvironment.Developer
                ? await ImportDeveloperAsync(projectName, p, backupFilePath, reporter, ct)
                : await ImportProductionAsync(p, backupFilePath, reporter, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import failed");
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Restores into the local Docker SQL Server: .bak via RESTORE, .bacpac via SqlPackage.</summary>
    private async Task<Result> ImportDeveloperAsync(string projectName, DnnProject p, string backupFilePath,
        IProgressReporter reporter, CancellationToken ct)
    {
        // Restore into the database the site uses (web.config SiteSqlServer), falling back to the
        // conventional {project}_dnndev name. This keeps backup and restore symmetric - otherwise a
        // backup of DB 'A' could be restored into a differently-named DB 'B', leaving the live site DB
        // untouched. The server is always the local Docker SQL Server (we connect as its sa).
        var dbName = DeveloperDb.FromWebConfig(p, _webConfig) ?? _opts.DatabaseNameFor(projectName);
        var port = await _docker.GetPublishedPortAsync(_opts.Docker.ContainerName, ct);
        if (port is null)
            return Result.Fail($"The shared SQL Server container '{_opts.Docker.ContainerName}' isn't running - start it and retry.");
        var db = _sqlContainer.DatabaseFor(p, dbName, port.Value);

        if (!await _prompt.ConfirmAsync(
                $"Restore [{db.DatabaseName}] from {Path.GetFileName(backupFilePath)} (overwrites the database)?", false, ct))
            return Result.Fail("Aborted by user.");

        reporter.Step($"Restoring [{db.DatabaseName}]");
        return await _sqlContainer.RestoreAsync(db, backupFilePath, reporter, ct);
    }

    /// <summary>
    /// Overwrites the production (Azure SQL) database from a .bacpac. Azure cannot RESTORE a native
    /// .bak, and SqlPackage import cannot overwrite an existing database - so this drops the existing
    /// database and reimports, preserving its edition/service objective so the tier is unchanged.
    /// </summary>
    private async Task<Result> ImportProductionAsync(DnnProject p, string backupFilePath,
        IProgressReporter reporter, CancellationToken ct)
    {
        // Production overwrite targets a remote (Azure) SQL Server. There is no stored env file any
        // more, so prompt for the connection up front (the login must be able to drop/create the DB).
        // Each prompt returns null on Esc/empty - bail immediately so cancelling the first field
        // doesn't drag the user through the rest.
        reporter.Info("Enter the production (Azure) SQL connection to overwrite (Esc to cancel).");
        var server = (await _prompt.PromptTextAsync("Production SQL server (e.g. myserver.database.windows.net)", ct))?.Trim();
        if (string.IsNullOrWhiteSpace(server)) return Result.Fail("Aborted by user.");
        var dbName = (await _prompt.PromptTextAsync("Production database name", ct))?.Trim();
        if (string.IsNullOrWhiteSpace(dbName)) return Result.Fail("Aborted by user.");
        var user = (await _prompt.PromptTextAsync("Production SQL admin user", ct))?.Trim();
        if (string.IsNullOrWhiteSpace(user)) return Result.Fail("Aborted by user.");
        var pass = await _prompt.PromptTextAsync("Production SQL admin password", ct);
        if (string.IsNullOrWhiteSpace(pass)) return Result.Fail("Aborted by user.");

        // Azure SQL can only be seeded from a BACPAC; a native .bak cannot be restored there.
        if (!backupFilePath.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase))
            return Result.Fail("Azure SQL can only be overwritten from a .bacpac file (native .bak RESTORE is not " +
                               "supported by Azure SQL). Back up a .bacpac first, then overwrite production with it.");
        var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
        if (!ensured.Success) return ensured;

        var target = new SiteSqlConnection(server, dbName, user, pass);
        var fileName = Path.GetFileName(backupFilePath);

        reporter.Step($"Connecting to production server {server}");
        var info = await _remoteAdmin.InspectAsync(target, ct);
        if (!info.Success || info.Value is null)
            return Result.Fail($"Could not connect to production server {server}: {info.Error}");

        // Spell out exactly what is at stake, then gate the destructive path behind an explicit
        // confirmation AND a typed database name - this drops the LIVE production database.
        reporter.Info($"Target: [{dbName}] on {server}  (Azure={info.Value.IsAzure}, " +
                      $"edition={info.Value.Edition ?? "n/a"}, SLO={info.Value.ServiceObjective ?? "n/a"}, " +
                      $"exists={info.Value.Exists}).");
        if (!await _prompt.ConfirmAsync(
                $"OVERWRITE PRODUCTION: replace [{dbName}] on {server} with {fileName}? This affects the LIVE site.", false, ct))
            return Result.Fail("Aborted by user.");
        var typed = await _prompt.PromptTextAsync($"Type the database name '{dbName}' to confirm", ct);
        if (!string.Equals(typed, dbName, StringComparison.Ordinal))
            return Result.Fail("Aborted: the typed name did not match.");

        // Preserve the existing Azure tier so the recreated database keeps the same edition/SLO
        // (otherwise SqlPackage creates it at the subscription default). An elastic-pool database
        // reports its objective as the literal 'ElasticPool', which SqlPackage cannot honour without
        // a pool name - skip the tier props in that case and let it create standalone at the default.
        var pooled = string.Equals(info.Value.ServiceObjective, "ElasticPool", StringComparison.OrdinalIgnoreCase);
        var props = new Dictionary<string, string>();
        if (info.Value.IsAzure && info.Value.Exists && !pooled)
        {
            if (!string.IsNullOrWhiteSpace(info.Value.Edition)) props["DatabaseEdition"] = info.Value.Edition!;
            if (!string.IsNullOrWhiteSpace(info.Value.ServiceObjective)) props["DatabaseServiceObjective"] = info.Value.ServiceObjective!;
        }
        else if (info.Value.IsAzure && info.Value.Exists && pooled)
        {
            reporter.Info("Source is in an elastic pool - the new copy will be created standalone at the default " +
                          "tier. Move it back into the pool in Azure after the overwrite if required.");
        }
        var sqlPackageProps = props.Count > 0 ? props : null;

        // First push (no existing database) - nothing to protect; import straight into the target.
        if (!info.Value.Exists)
        {
            var firstImport = await _bacpac.ImportAsync(server, user, pass, dbName, backupFilePath, reporter, ct, sqlPackageProps);
            if (!firstImport.Success)
            {
                // A failed Azure import can leave a partial DB under the production name - drop it.
                await TryDropAsync(target, info.Value.IsAzure, reporter);
                return firstImport;
            }
            reporter.Success($"Production database [{dbName}] on {server} created from {fileName}.");
            WarnAppLoginRemap(dbName, reporter);
            return Result.Ok();
        }

        // Existing production database: import into a TEMPORARY database first so a failed import
        // leaves production fully intact (SqlPackage can't import over an existing DB). Only once the
        // import fully succeeds do we drop the old database and rename the new copy into place.
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tempName = $"{dbName}_import_{stamp}";
        var tempTarget = target with { Database = tempName };

        var oldDropped = false; // once true, tempTarget holds the ONLY copy - it must never be dropped.
        var swapped = false;
        try
        {
            reporter.Info($"Importing into temporary database [{tempName}] - production stays live until the import succeeds.");
            var import = await _bacpac.ImportAsync(server, user, pass, tempName, backupFilePath, reporter, ct, sqlPackageProps);
            if (!import.Success) return import; // finally drops the temp DB; production untouched.

            // Import succeeded - replace production with the freshly imported copy.
            var dropOld = await _remoteAdmin.DropDatabaseAsync(target, info.Value.IsAzure, reporter, ct);
            if (!dropOld.Success)
                return Result.Fail($"New data imported as [{tempName}], but the old production database could not be " +
                                   $"dropped: {dropOld.Error}. Production is unchanged - resolve the issue and retry.");
            oldDropped = true; // POINT OF NO RETURN: the production name is now free; temp is the only copy.

            // Rename temp → prod. We are past the point of no return, so push hard: retry with backoff
            // and ignore cancellation (abandoning here would leave production with no database at all).
            var rename = await RenameWithRetryAsync(target, tempName, dbName, reporter);
            if (!rename.Success)
                return Result.Fail($"OUTAGE: production database [{dbName}] on {server} is currently DOWN. The new data " +
                                   $"imported successfully but is still named [{tempName}], and renaming it failed: {rename.Error}. " +
                                   $"Restore service now by running:  ALTER DATABASE [{tempName}] MODIFY NAME = [{dbName}];  on {server}.");

            swapped = true;
            reporter.Success($"Production database [{dbName}] on {server} overwritten from {fileName}.");
            WarnAppLoginRemap(dbName, reporter);
            return Result.Ok();
        }
        finally
        {
            // Drop the temp DB only while it is still disposable (the live production DB still exists).
            // After the old DB is dropped, tempTarget is the sole copy and must never be deleted here.
            if (!swapped && !oldDropped)
                await TryDropAsync(tempTarget, info.Value.IsAzure, reporter);
        }
    }

    /// <summary>
    /// Renames a freshly imported temp database into the production name. Runs past the point of no
    /// return (the old DB is already gone), so it uses <see cref="CancellationToken.None"/> and retries
    /// with backoff - a transient Azure control-plane failure must not abandon the swap mid-flight.
    /// </summary>
    private async Task<Result> RenameWithRetryAsync(SiteSqlConnection conn, string fromName, string toName, IProgressReporter reporter)
    {
        Result last = Result.Fail("rename not attempted");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            last = await _remoteAdmin.RenameDatabaseAsync(conn, fromName, toName, reporter, CancellationToken.None);
            if (last.Success) return last;
            if (attempt < 5)
            {
                reporter.Info($"Rename attempt {attempt}/5 failed; retrying in {2 * attempt}s…");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), CancellationToken.None);
            }
        }
        return last;
    }

    /// <summary>Best-effort drop that never throws and runs even when the caller's token is cancelled.</summary>
    private async Task TryDropAsync(SiteSqlConnection target, bool isAzure, IProgressReporter reporter)
    {
        try { await _remoteAdmin.DropDatabaseAsync(target, isAzure, reporter, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "Best-effort drop of [{Db}] failed", target.Database); }
    }

    private static void WarnAppLoginRemap(string dbName, IProgressReporter reporter) =>
        reporter.Info($"Note: [{dbName}] now carries the source backup's database users, not production's. If the live " +
                      "site connects with a SQL login that isn't in the .bacpac, recreate that user on the database " +
                      "(CREATE USER … FOR LOGIN …; ALTER ROLE db_owner ADD MEMBER …) or the site may fail to connect.");
}
