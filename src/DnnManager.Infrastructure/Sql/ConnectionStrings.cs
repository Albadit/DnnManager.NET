using DnnManager.Application.Abstractions;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Connection strings for a <see cref="DatabaseConnection"/>, made with <see cref="SqlConnectionStringBuilder"/> so a
/// password with <c>;</c>, <c>=</c> or quotes in it can't break them.
/// </summary>
public static class ConnectionStrings
{
    /// <summary>
    /// The <c>SiteSqlServer</c> connection string DNN reads from web.config. DNN runs on .NET Framework's
    /// System.Data.SqlClient, so only keywords both clients know are used (no Encrypt / Trust Server Certificate).
    /// </summary>
    public static string ForSite(DatabaseConnection connection)
    {
        var builder = new SqlConnectionStringBuilder { DataSource = connection.Server };
        if (connection.Kind == DatabaseKind.LocalDbFile)
        {
            // DNN's "SQL Server Express File", but without User Instance - LocalDB refuses that.
            builder.AttachDBFilename = $"|DataDirectory|{connection.Database}";
            builder.IntegratedSecurity = true;
            // The site's first connection may have to create the LocalDB instance of its identity - that takes a while.
            builder.ConnectTimeout = 60;
            return builder.ConnectionString;
        }

        builder.InitialCatalog = connection.Database;
        if (connection.UsesWindowsAuthentication)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = connection.User;
            builder.Password = connection.Password;
        }
        return builder.ConnectionString;
    }

    /// <summary>
    /// DNN Manager's own connection to <paramref name="database"/> (the connection's database when null) on the
    /// connection's server. A LocalDB file database is attached by its full path under <paramref name="siteDirectory"/>.
    /// </summary>
    public static string ForApp(DatabaseConnection connection, string? database = null, int timeoutSeconds = 15,
        string? siteDirectory = null)
    {
        var attach = connection.Kind == DatabaseKind.LocalDbFile && database is null && siteDirectory is not null;
        var builder = For(connection.Server, attach ? null : database ?? connection.Database,
            connection.UsesWindowsAuthentication ? "" : connection.User, connection.Password, timeoutSeconds);
        if (attach) builder.AttachDBFilename = Path.Combine(siteDirectory!, "App_Data", connection.Database);
        return builder.ConnectionString;
    }

    /// <summary>
    /// Every connection DNN Manager itself makes, to <paramref name="database"/> (none: the login's default) on
    /// <paramref name="server"/> - as <paramref name="user"/>, or with Windows authentication as whoever runs DNN Manager
    /// when it is empty. The one place the encryption is decided: Azure SQL requires TLS, so it is always on - but for
    /// LocalDB, which is only ever on this machine; a server on this PC (the container, a local instance) has a
    /// self-signed certificate, taken as it is; one elsewhere must show a certificate Windows trusts
    /// (<see cref="TrustServerCertificate"/>). The caller adds what is its own (no pooling, an application name).
    /// </summary>
    public static SqlConnectionStringBuilder For(string server, string? database, string user, string? password, int timeoutSeconds)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            Encrypt = IsLocalDb(server) ? SqlConnectionEncryptOption.Optional : SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = TrustServerCertificate(server),
            ConnectTimeout = timeoutSeconds
        };
        if (!string.IsNullOrEmpty(database)) builder.InitialCatalog = database;
        if (user.Length == 0)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = user;
            builder.Password = password ?? "";
        }
        return builder;
    }

    /// <summary>True for a LocalDB instance, e.g. <c>(LocalDB)\MSSQLLocalDB</c>.</summary>
    public static bool IsLocalDb(string server) => server.TrimStart().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="server"/>'s certificate is taken without checking it: only on this computer (the container,
    /// LocalDB, a local instance), whose certificates are self-signed and where nobody sits in between. A server elsewhere
    /// - a clone's live source, Azure SQL - must show a certificate Windows trusts: without that check anyone on the way
    /// could read the login and change the data (TLS without knowing who the other end is).
    /// </summary>
    public static bool TrustServerCertificate(string server) => SqlServerAddress.IsOnThisMachine(server);

    /// <summary><paramref name="message"/> - with what to do when it is about a certificate Windows doesn't trust.</summary>
    public static string Explained(string message) =>
        message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            ? message + " DNN Manager checks the certificate of a SQL Server on another computer. Use the name its certificate " +
                        "is made out to, or - for a server of your own with a self-signed certificate - import that certificate into " +
                        "this computer's Trusted Root Certification Authorities (certlm.msc)."
            : message;

    /// <summary>
    /// Reads web.config's <c>SiteSqlServer</c> into a connection: integrated security or a SQL login, and
    /// <c>AttachDbFilename</c> for a LocalDB file. Whether a server is the local container is the caller's to decide.
    /// </summary>
    public static DatabaseConnection? Parse(string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { return null; }

        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            var file = builder.AttachDBFilename.Replace("|DataDirectory|", "", StringComparison.OrdinalIgnoreCase).TrimStart('\\', '/');
            return new DatabaseConnection(DatabaseKind.LocalDbFile, builder.DataSource, Path.GetFileName(file), SqlAuthentication.Windows);
        }
        if (string.IsNullOrWhiteSpace(builder.DataSource)) return null;
        return builder.IntegratedSecurity
            ? new DatabaseConnection(DatabaseKind.SqlServer, builder.DataSource, builder.InitialCatalog, SqlAuthentication.Windows)
            : new DatabaseConnection(DatabaseKind.SqlServer, builder.DataSource, builder.InitialCatalog, SqlAuthentication.Sql,
                builder.UserID, builder.Password);
    }
}
