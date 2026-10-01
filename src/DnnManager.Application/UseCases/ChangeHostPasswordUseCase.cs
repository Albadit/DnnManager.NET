using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Sets a new password for one of a DNN site's host accounts, the way DNN's membership provider stores passwords, then
/// restarts the site (DNN caches its users). A LocalDB file database can only be opened while the site doesn't use it,
/// so that site is stopped meanwhile - and started again only if it ran.
/// </summary>
public sealed class ChangeHostPasswordUseCase
{
    private readonly IIisManager _iis;
    private readonly IDnnInstaller _dnn;
    private readonly IProjectRepository _projects;
    private readonly LocalSqlContainer _sql;

    public ChangeHostPasswordUseCase(IIisManager iis, IDnnInstaller dnn, IProjectRepository projects, LocalSqlContainer sql)
    {
        _iis = iis; _dnn = dnn; _projects = projects; _sql = sql;
    }

    public async Task<Result> ExecuteAsync(string siteName, string siteDirectory, string userName, string newPassword,
        IProgressReporter reporter, CancellationToken ct)
    {
        if (DnnAccountRules.PasswordProblem(newPassword) is { } problem) return Result.Fail(problem);
        var database = _sql.DatabaseOf(_projects.Build(siteName, siteDirectory));
        if (database is null) return Result.Fail("The site's web.config doesn't name its database yet - install DNN first.");

        reporter.Step($"Changing the password of host '{userName}'");
        var localFile = database.Kind == DatabaseKind.LocalDbFile;
        var wasRunning = _iis.GetSiteStates().TryGetValue(siteName, out var state) && state.Equals("Started", StringComparison.OrdinalIgnoreCase);
        if (localFile && wasRunning)
        {
            reporter.Info("Stopping the site for a moment - its database file can only be opened while the site doesn't use it…");
            _iis.StopSite(siteName);
        }

        Result changed;
        try
        {
            changed = await _dnn.ChangeHostPasswordAsync(siteDirectory, database, userName, newPassword, ct);
        }
        finally
        {
            if (localFile && wasRunning) _iis.StartSite(siteName);
        }
        if (!changed.Success) return changed;

        // DNN keeps its users in memory: it reads the account again after a restart.
        if (!localFile) _iis.RecycleAppPool(siteName);
        reporter.Success($"Host '{userName}' signs in with the new password - it is kept nowhere else.");
        return Result.Ok();
    }
}