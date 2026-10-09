using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface ISqlServerService
{
    Task<Result<bool>> DatabaseExistsAsync(string database, CancellationToken ct);
    Task<Result> CreateDatabaseAsync(DatabaseConfig db, CancellationToken ct);
    Task<Result> DropDatabaseAsync(string database, CancellationToken ct);
    Task<Result<string>> BackupDatabaseLocalAsync(string database, string backupFileName, CancellationToken ct);
    Task<Result> RestoreDatabaseLocalAsync(DatabaseConfig db, string backupFilePath, CancellationToken ct);

    /// <summary>
    /// Rewrites every PortalAlias whose HTTPAlias is a host name ending with <paramref name="hostnameSuffix"/> (with or
    /// without a port) so that portal 0's primary alias becomes <paramref name="newAlias"/> - the host name, with
    /// <c>:port</c> when the site isn't on port 80 (<see cref="DnnSiteAddress.AliasFor"/>). Used after cloning so the new
    /// site responds at its own address instead of the source's. DNN's objectQualifier is found from the tables.
    /// </summary>
    Task<Result> RemapPortalAliasesAsync(string database, string hostnameSuffix, string newAlias, CancellationToken ct);

    /// <summary>
    /// Turns DNN's SSL off in <paramref name="database"/> - the portal's SSL setting and pages marked secure - so the
    /// local site, which only has an http binding, isn't redirected to https. Returns how many values it changed. DNN's
    /// objectQualifier is found from the tables.
    /// </summary>
    Task<Result<int>> DisableSslAsync(string database, CancellationToken ct);

    /// <summary>
    /// The SQL login <paramref name="login"/> with <paramref name="password"/> - made, or its password set - and its user
    /// in <paramref name="database"/>, made or mapped to it again (a restored database brings a user of its own), owner
    /// (db_owner) of that database and of nothing else. What a site signs in with: not sa, which could reach every
    /// project's database.
    /// </summary>
    Task<Result> GrantSiteLoginAsync(string database, string login, string password, CancellationToken ct);

    /// <summary>Drops the SQL login <paramref name="login"/>; Ok when there is none.</summary>
    Task<Result> DropLoginAsync(string login, CancellationToken ct);
}
