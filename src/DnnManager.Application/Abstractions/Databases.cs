using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Where a site's database lives.</summary>
public enum DatabaseKind
{
    /// <summary>The local SQL Server container (Docker) from the settings - signed in to as its user (sqlServer.user, sa by default).</summary>
    Container,

    /// <summary>Any SQL Server or SQL Server Express instance, with Windows or SQL Server authentication.</summary>
    SqlServer,

    /// <summary>
    /// SQL Server Express LocalDB attaching the site's own <c>App_Data\Database.mdf</c> - DNN's "SQL Server Express
    /// File". The database runs in the LocalDB instance of whoever the site runs as.
    /// </summary>
    LocalDbFile
}

public enum SqlAuthentication
{
    /// <summary>Integrated security: the site signs in as its app pool's Windows identity.</summary>
    Windows,

    /// <summary>A SQL Server login with a user name and password.</summary>
    Sql
}

/// <summary>
/// A site's database: where it is and how the site signs in to it. The password is only ever held in memory, to
/// write web.config and to sign in - <see cref="ToString"/> and <see cref="Describe"/> never show it.
/// </summary>
/// <param name="Database">The database name; for <see cref="DatabaseKind.LocalDbFile"/> the .mdf file in App_Data.</param>
public sealed record DatabaseConnection(
    DatabaseKind Kind,
    string Server,
    string Database,
    SqlAuthentication Authentication,
    string User = "",
    string Password = "")
{
    /// <summary>LocalDB's automatic instance - the one every Windows user has.</summary>
    public const string LocalDbServer = @"(LocalDB)\MSSQLLocalDB";

    /// <summary>The empty database file DNN's install package ships in App_Data.</summary>
    public const string LocalDbFileName = "Database.mdf";

    public bool UsesWindowsAuthentication => Kind == DatabaseKind.LocalDbFile || Authentication == SqlAuthentication.Windows;

    /// <summary>"[db] on server, as user" - for messages; never the password.</summary>
    public string Describe()
    {
        var who = UsesWindowsAuthentication ? "Windows authentication" : $"user '{User}'";
        return Kind == DatabaseKind.LocalDbFile
            ? $"App_Data\\{Database} with {Server} ({who})"
            : $"[{Database}] on {Server} ({who})";
    }

    /// <summary>The kind of connection in words, as the overview and the settings name it.</summary>
    public string KindText => Kind switch
    {
        DatabaseKind.Container => "Local SQL container (Docker)",
        DatabaseKind.LocalDbFile => "SQL Server Express LocalDB (file)",
        _ => "SQL Server"
    };

    public string AuthenticationText => UsesWindowsAuthentication ? "Windows authentication" : "SQL Server authentication";

    // A record prints every property by default - the password must never end up in a message or a log.
    public override string ToString() => Describe();
}

public enum CheckOutcome { Passed, Warning, Failed }

/// <param name="Name">What was checked, e.g. "Server reachable".</param>
/// <param name="Detail">What was found, e.g. "SQL Server 2022 (16.0.4255)" - or why it failed and what to do.</param>
public sealed record DatabaseCheck(string Name, CheckOutcome Outcome, string Detail);

/// <summary>The checks Test connection ran, in order; it stops at the first one the rest depend on.</summary>
public sealed record DatabaseCheckReport(IReadOnlyList<DatabaseCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Outcome != CheckOutcome.Failed);

    /// <summary>The first failed check's detail, or null when nothing failed.</summary>
    public string? Problem => Checks.FirstOrDefault(c => c.Outcome == CheckOutcome.Failed) is { } failed
        ? $"{failed.Name}: {failed.Detail}"
        : null;
}

/// <summary>What a connection test is for, so it checks what that needs.</summary>
/// <param name="ForNewInstall">
/// A new DNN install goes into the database: it must not hold a DNN site already, and the login must be able to
/// create it (or own it when it exists).
/// </param>
/// <param name="SiteLogin">
/// With Windows authentication, the login the site will sign in as (<c>IIS APPPOOL\&lt;pool&gt;</c>) - checked to exist,
/// or that it can be created. Null to skip.
/// </param>
/// <param name="MinimumMajorVersion">The oldest SQL Server the DNN version supports (14 = SQL Server 2017 for DNN 10).</param>
/// <param name="ServerOnly">
/// Only the server (Settings → Database server): reachable, signed in, the version, and whether the login may create the
/// databases new projects get - no particular database, so the connection's database name isn't needed.
/// </param>
public sealed record DatabaseCheckOptions(bool ForNewInstall, string? SiteLogin = null, int MinimumMajorVersion = 14, bool ServerOnly = false);

/// <summary>Creates, checks, grants and drops site databases on any SQL Server - with SqlClient, not the container's tools.</summary>
public interface IDatabaseProvisioner
{
    /// <summary>
    /// Test connection: is the server there, can we sign in, is it new enough, and can the database be created (or
    /// is it usable as it is). Never throws; every problem is a failed check with a message that says what to do.
    /// </summary>
    Task<DatabaseCheckReport> CheckAsync(DatabaseConnection connection, DatabaseCheckOptions options, CancellationToken ct);

    Task<Result<bool>> DatabaseExistsAsync(DatabaseConnection connection, CancellationToken ct);

    /// <summary>Creates the (empty) database, with <paramref name="collation"/> when given.</summary>
    Task<Result> CreateDatabaseAsync(DatabaseConnection connection, string? collation, CancellationToken ct);

    /// <summary>
    /// Lets the Windows login <paramref name="windowsLogin"/> (e.g. <c>IIS APPPOOL\site</c>) own the database: creates
    /// the login and its database user when missing, and adds it to db_owner.
    /// </summary>
    Task<Result> GrantSiteAccessAsync(DatabaseConnection connection, string windowsLogin, CancellationToken ct);

    /// <summary>Drops the database, closing the connections still open to it.</summary>
    Task<Result> DropDatabaseAsync(DatabaseConnection connection, CancellationToken ct);

    /// <summary>The site's DNN host accounts (superusers): user name and e-mail, oldest first.</summary>
    Task<Result<IReadOnlyList<DnnHostAccount>>> ListHostAccountsAsync(DatabaseConnection connection, CancellationToken ct);
}

public sealed record DnnHostAccount(int UserId, string UserName, string Email);

/// <summary>
/// Secrets DNN Manager keeps between runs (the default host password, the database server login's password) - in the Windows
/// Credential Manager of the signed-in user, never in settings.json.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret stored under <paramref name="name"/>, or null when there is none.</summary>
    string? Read(string name);

    Result Write(string name, string secret);

    /// <summary>Removes it; Ok when there was nothing to remove.</summary>
    Result Delete(string name);
}

/// <summary>The names secrets are kept under in <see cref="ISecretStore"/>.</summary>
public static class SecretNames
{
    public const string DefaultHostPassword = "dnn-defaults/host-password";

    /// <summary>The password of the SQL Server login new projects use (Settings → Database server, SQL Server authentication).</summary>
    public const string DatabaseServerPassword = "database-server/password";

    /// <summary>Where DNN Manager 2.3.0 kept a database profile's password - moved or removed when the settings are upgraded.</summary>
    public static string LegacyDatabaseProfilePassword(string profileId) => $"database-profile/{profileId}";
}

/// <summary>
/// The DNN host (superuser) account and website an automatic install creates. The password is held in memory only.
/// </summary>
/// <param name="WebsiteName">The portal's name, e.g. "My Website".</param>
/// <param name="Language">The install culture, e.g. "en-US" - another one has DNN download its language pack.</param>
/// <param name="Template">The site template's name without ".template", e.g. "Default Website".</param>
public sealed record DnnAccount(string UserName, string Password, string Email, string WebsiteName, string Language, string Template)
{
    public override string ToString() => $"host '{UserName}' <{Email}>, website '{WebsiteName}' ({Language}, {Template})";
}
