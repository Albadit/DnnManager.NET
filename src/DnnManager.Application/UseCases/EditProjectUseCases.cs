using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Edit host names: the site's http bindings as the dialog left them, with DNN's portal aliases following - a changed
/// host name's alias is renamed, a new one's added. Its https and other bindings stay. A LocalDB file database can only
/// be opened while the site doesn't use it, so that site is stopped meanwhile and started again if it ran.
/// </summary>
public sealed class EditBindingsUseCase(IIisManager iis, IProjectRepository projects, LocalSqlContainer sql,
    IDatabaseProvisioner databases, OperationUndo undo, IUserPrompt prompt, IOptions<AppOptions> options)
{
    private readonly IUserPrompt _prompt = prompt;
    private readonly AppOptions _options = options.Value;
    private readonly IIisManager _iis = iis;
    private readonly IProjectRepository _projects = projects;
    private readonly LocalSqlContainer _sql = sql;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly OperationUndo _undo = undo;

    public async Task<Result> ExecuteAsync(string siteName, string directory, IReadOnlyList<HttpBindingEdit> bindings,
        IProgressReporter reporter, CancellationToken ct)
    {
        if (bindings.Count == 0) return Result.Fail("A site needs at least one http binding.");
        if (_iis.GetSiteDetails(siteName) is not { } site) return Result.Fail($"IIS has no site named '{siteName}'.");
        var before = site.Bindings.Where(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port is not null)
            .Select(b => (b.Host, Port: b.Port!.Value)).ToList();

        reporter.Step("Changing the bindings");
        _undo.Add("Put the bindings back", () => _iis.ReplaceHttpBindings(siteName, before));
        var bound = _iis.ReplaceHttpBindings(siteName, bindings.Select(b => (b.Host, b.Port)).ToList());
        if (!bound.Success) return Result.Fail($"Could not change the bindings: {bound.Error}");
        foreach (var b in bindings) reporter.Success($"http://{(b.Host.Length > 0 ? DnnSiteAddress.AliasFor(b.Host, b.Port) : $"*:{b.Port}")}");

        // An alias is a host name with the port when it isn't 80; a binding to any host has none.
        var renamed = bindings
            .Where(b => b.OldHost is { Length: > 0 } && b.Host.Length > 0 && b.OldPort is not null &&
                        (!b.OldHost.Equals(b.Host, StringComparison.OrdinalIgnoreCase) || b.OldPort != b.Port))
            .Select(b => (DnnSiteAddress.AliasFor(b.OldHost!, b.OldPort!.Value), DnnSiteAddress.AliasFor(b.Host, b.Port)))
            .ToList();
        var added = bindings.Where(b => b.OldHost is null && b.Host.Length > 0).Select(b => DnnSiteAddress.AliasFor(b.Host, b.Port)).ToList();
        if (renamed.Count + added.Count == 0) return Result.Ok();

        reporter.Step("DNN's portal aliases");
        var database = _sql.DatabaseOf(_projects.Build(siteName, directory));
        if (database is null)
        {
            reporter.Warn("web.config names no database - DNN's portal aliases stay as they are. Add the new host names as site aliases in DNN.");
            return Result.Ok();
        }
        // The database web.config names - the site's app pool can change it - gets DNN's aliases changed with DNN Manager's
        // rights: asked first when it isn't the project's own, or when signing in to it as you would hand your Windows
        // sign-in to a server elsewhere.
        if (await DatabaseRefusedAsync(siteName, database, ct) is { } kept)
        {
            reporter.Warn($"{kept} - DNN's portal aliases stay as they are. Add the new host names as site aliases in DNN.");
            return Result.Ok();
        }
        var localFile = database.Kind == DatabaseKind.LocalDbFile;
        var wasRunning = IisStates.IsStarted(site.State);
        if (localFile && wasRunning)
        {
            reporter.Info("Stopping the site for a moment - its database file can only be opened while the site doesn't use it…");
            _iis.StopSite(siteName);
        }
        try
        {
            _undo.Add("Put DNN's portal aliases back", async () => (await _databases.UpdatePortalAliasesAsync(database, directory,
                renamed.Select(r => (r.Item2, r.Item1)).ToList(), [], CancellationToken.None)).WithoutValue());
            var updated = await _databases.UpdatePortalAliasesAsync(database, directory, renamed, added, ct);
            if (updated.Success) reporter.Success($"{updated.Value} portal alias(es) changed or added.");
            else reporter.Warn($"{updated.Error} - change the site aliases in DNN (Settings → Site Settings → Site Aliases).");
        }
        finally
        {
            if (localFile && wasRunning) _iis.StartSite(siteName);
        }
        // DNN keeps its aliases in memory: it reads them again after a restart.
        if (!localFile) _iis.RecycleAppPool(siteName);
        return Result.Ok();
    }

    /// <summary>
    /// Why DNN's aliases aren't changed in <paramref name="database"/> - the user said no: it isn't the project's own (named
    /// like it), or it is reached with their Windows account on a server not on this PC and not the one in Settings;
    /// null when they may be.
    /// </summary>
    private async Task<string?> DatabaseRefusedAsync(string siteName, DatabaseConnection database, CancellationToken ct)
    {
        if (database.Kind == DatabaseKind.LocalDbFile) return null;
        var nl = Environment.NewLine;
        var own = _options.DatabaseNameFor(siteName);
        if (!database.Database.Equals(own, StringComparison.OrdinalIgnoreCase) &&
            !await _prompt.ConfirmAsync($"The web.config of '{siteName}' names the database [{database.Database}] on {database.Server} - not the " +
                                        $"project's own, [{own}].{nl}{nl}Change DNN's portal aliases in [{database.Database}]?",
                                        "Change aliases there", "Leave them", false, ct))
            return $"[{database.Database}] isn't the project's own database, and was left as it is";
        if (database.Authentication == SqlAuthentication.Windows && !SqlServerAddress.MaySignInAsUser(database.Server, _options.DatabaseServer) &&
            !await _prompt.ConfirmAsync(SqlServerAddress.SignInQuestion(database.Server, "Changing DNN's portal aliases"),
                                        $"Sign in to {database.Server}", "Don't sign in", false, ct))
            return $"Not signed in to {database.Server} with your Windows account";
        return null;
    }
}

/// <summary>Edit app pool: the settings of the site's own app pool; the site's folder lets a new identity in. Recycles the pool.</summary>
public sealed class EditAppPoolUseCase(IIisManager iis, OperationUndo undo)
{
    private readonly IIisManager _iis = iis;
    private readonly OperationUndo _undo = undo;

    public Task<Result> ExecuteAsync(string siteName, string directory, IisPoolSettings settings, IProgressReporter reporter, CancellationToken ct)
    {
        if (_iis.GetSiteDetails(siteName) is not { Pool: { } pool }) return Task.FromResult(Result.Fail($"IIS site '{siteName}' has no app pool."));
        var before = IisPoolSettings.From(pool);

        reporter.Step($"Changing the app pool '{pool.Name}'");
        _undo.Add("Put the app pool's settings back", () => _iis.SetPoolSettings(siteName, before));
        var set = _iis.SetPoolSettings(siteName, settings);
        if (!set.Success) return Task.FromResult(Result.Fail($"Could not change the app pool: {set.Error}"));
        reporter.Success($"{(settings.Runtime.Length > 0 ? $".NET CLR {settings.Runtime}" : "No managed code")}, {settings.Pipeline}, " +
                         $"{(settings.Enable32Bit ? "32-bit" : "64-bit")}, {settings.Identity}, idle time-out " +
                         $"{(settings.IdleTimeout <= TimeSpan.Zero ? "none" : $"{settings.IdleTimeout.TotalMinutes:0} min")}, {settings.StartMode}.");

        if (!settings.Identity.Equals(before.Identity, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
        {
            var account = _iis.AppPoolIdentity(siteName);
            var grant = _iis.GrantPermissions(directory, [account]);
            if (grant.Success) reporter.Success($"{account} may use {directory}.");
            else reporter.Warn($"Could not give {account} access to {directory}: {grant.Error}");
            reporter.Info("With Windows authentication its database needs that account as a login too - Details → Database → Change connection grants it.");
        }

        _iis.RecycleAppPool(siteName);
        reporter.Success("The app pool was recycled - the site starts with the new settings.");
        return Task.FromResult(Result.Ok());
    }
}

/// <summary>
/// Change connection: points web.config's SiteSqlServer at another database, server or sign-in. Nothing in the databases
/// is moved - the new one should already hold the site. With Windows authentication the site's app pool gets a login,
/// with a LocalDB file its user profile is loaded. Restarts the site.
/// </summary>
public sealed class ChangeDatabaseConnectionUseCase(IIisManager iis, IWebConfigService webConfig, IDatabaseProvisioner databases, OperationUndo undo)
{
    private readonly IIisManager _iis = iis;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly OperationUndo _undo = undo;

    public async Task<Result> ExecuteAsync(string siteName, string directory, DatabaseConnection connection,
        IProgressReporter reporter, CancellationToken ct)
    {
        var webConfigPath = Path.Combine(directory, "web.config");
        if (!File.Exists(webConfigPath)) return Result.Fail($"The site has no web.config: {webConfigPath}");

        reporter.Step("Checking the database");
        var account = _iis.AppPoolIdentity(siteName);
        var report = await _databases.CheckAsync(connection,
            new DatabaseCheckOptions(ForNewInstall: false, SiteLogin: connection.UsesWindowsAuthentication && connection.Kind != DatabaseKind.LocalDbFile ? account : null), ct);
        foreach (var check in report.Checks)
        {
            var line = $"{check.Name}: {check.Detail}";
            if (check.Outcome == CheckOutcome.Passed) reporter.Success(line);
            else if (check.Outcome == CheckOutcome.Warning) reporter.Warn(line);
            else reporter.Fail(line);
        }
        if (!report.Passed) reporter.Warn("Saved anyway - the site shows an error until the database answers.");

        reporter.Step("Pointing web.config at it");
        _undo.RestoreFileOnUndo(webConfigPath);
        var written = _webConfig.WriteDatabaseConnection(webConfigPath, connection);
        if (!written.Success) return Result.Fail($"Could not write web.config: {written.Error}");
        reporter.Success($"SiteSqlServer: {connection.Describe()}");

        if (connection.Kind == DatabaseKind.LocalDbFile)
        {
            var profile = _iis.EnableUserProfile(siteName);
            if (!profile.Success) reporter.Warn($"Could not load the app pool's user profile, which LocalDB needs: {profile.Error}");
        }
        else if (connection.UsesWindowsAuthentication && report.Passed)
        {
            var access = await _databases.GrantSiteAccessAsync(connection, account, ct);
            if (access.Success) reporter.Success($"{account} may use [{connection.Database}].");
            else reporter.Warn(access.Error!);
        }

        _iis.RecycleAppPool(siteName);
        reporter.Success("The site was restarted with the new connection.");
        return Result.Ok();
    }
}
