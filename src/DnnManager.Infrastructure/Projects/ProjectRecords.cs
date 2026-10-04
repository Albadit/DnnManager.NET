using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Projects;

/// <summary>
/// <see cref="ProjectRecord"/>s in the <c>projects</c> table of DNN Manager's database (<see cref="AppDatabase"/>), one
/// row per project. They hold no secrets. One that can't be read counts as no record - it is only what DNN Manager
/// remembers, never the site.
/// </summary>
public sealed class ProjectRecords(AppDatabase database, ILogger<ProjectRecords> log) : IProjectRecords
{
    private readonly AppDatabase _database = database;
    private readonly ILogger<ProjectRecords> _log = log;

    public ProjectRecord? Find(string site)
    {
        try
        {
            using var connection = _database.Open();
            using var command = AppDatabase.Command(connection,
                "SELECT site, install_mode, created_utc, dnn_version, host_user_name FROM projects WHERE site = $site", ("$site", site));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new ProjectRecord(reader.GetString(0), Enum.Parse<DnnInstallMode>(reader.GetString(1), ignoreCase: true),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            _log.LogWarning(ex, "Could not read the record of {Site}", site);
            return null;
        }
    }

    public void Save(ProjectRecord record)
    {
        try
        {
            using var connection = _database.Open();
            Insert(connection, record, replace: true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save the record of {Site}", record.Site);
        }
    }

    public void Remove(string site)
    {
        try
        {
            using var connection = _database.Open();
            AppDatabase.Execute(connection, "DELETE FROM projects WHERE site = $site", ("$site", site));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove the record of {Site}", site);
        }
    }

    /// <summary>Writes <paramref name="record"/> - over the site's one, or (not <paramref name="replace"/>) only when it has none.</summary>
    internal static void Insert(SqliteConnection connection, ProjectRecord record, bool replace) =>
        AppDatabase.Execute(connection,
            $"INSERT OR {(replace ? "REPLACE" : "IGNORE")} INTO projects (site, install_mode, created_utc, dnn_version, host_user_name) " +
            "VALUES ($site, $mode, $created, $version, $host)",
            ("$site", record.Site), ("$mode", record.InstallMode.ToString()), ("$created", record.CreatedUtc.ToString("O")),
            ("$version", record.DnnVersion), ("$host", record.HostUserName));
}
