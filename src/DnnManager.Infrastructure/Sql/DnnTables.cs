using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// DNN's tables in its database: their names can start with an <c>objectQualifier</c> (<c>dnn_Users</c>), which is
/// found from the tables themselves. The one place that finds it - and the guard that keeps what is read from a
/// database out of the SQL built with it: a qualifier is letters, digits and <c>_</c>, anything else isn't DNN's.
/// </summary>
internal static class DnnTables
{
    /// <summary>
    /// The qualifier ("" for none, "dnn_") of the DNN table <paramref name="table"/> (a constant such as "Version"), or
    /// null when the database has no such table.
    /// </summary>
    public static async Task<string?> QualifierAsync(SqlConnection conn, string table, CancellationToken ct, SqlTransaction? transaction = null)
    {
        await using var find = new SqlCommand(
            "SELECT TOP 1 LEFT(name, LEN(name) - LEN(@table)) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'dbo' " +
            "AND (name = @table OR name LIKE '%[_]' + @table) ORDER BY LEN(name)", conn, transaction);
        find.Parameters.AddWithValue("@table", table);
        return await find.ExecuteScalarAsync(ct) is string qualifier && IsQualifier(qualifier) ? qualifier : null;
    }

    /// <summary>The table's name to put in SQL: <c>dbo.[dnn_Users]</c>.</summary>
    public static string Name(string qualifier, string table) => $"dbo.[{qualifier}{table}]";

    public static bool IsQualifier(string text) => text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
