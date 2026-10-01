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
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Server,
            // Like every other connection DNN Manager makes: Azure SQL requires TLS, local servers have self-signed
            // certificates. LocalDB is only ever on this machine.
            Encrypt = IsLocalDb(connection.Server) ? SqlConnectionEncryptOption.Optional : SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            ConnectTimeout = timeoutSeconds
        };
        if (connection.Kind == DatabaseKind.LocalDbFile && database is null && siteDirectory is not null)
            builder.AttachDBFilename = Path.Combine(siteDirectory, "App_Data", connection.Database);
        else
            builder.InitialCatalog = database ?? connection.Database;

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

    /// <summary>True for a LocalDB instance, e.g. <c>(LocalDB)\MSSQLLocalDB</c>.</summary>
    public static bool IsLocalDb(string server) => server.TrimStart().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase);

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
