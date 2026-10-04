using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Projects;

/// <summary>
/// The sites kept warm: a row each in the <c>keep_warm</c> table of DNN Manager's database (<see cref="AppDatabase"/>),
/// written the moment one is switched on or off - so the next start keeps the same sites warm. How they are kept warm is
/// the same for every site (Settings → Projects → Keep warm). When the database can't be read, no site counts as kept warm.
/// </summary>
public sealed class KeepWarmRecords(AppDatabase database, ILogger<KeepWarmRecords> log) : IKeepWarmRecords
{
    private readonly AppDatabase _database = database;
    private readonly ILogger<KeepWarmRecords> _log = log;

    public KeepWarmRecord? Find(string site) => Read("SELECT site FROM keep_warm WHERE site = $site", ("$site", site)).FirstOrDefault();

    public IReadOnlyList<KeepWarmRecord> List() => Read("SELECT site FROM keep_warm ORDER BY site");

    public void Save(KeepWarmRecord record)
    {
        if (record.IsEmpty) Remove(record.Site);
        else Write("INSERT OR IGNORE INTO keep_warm (site) VALUES ($site)", record.Site);
    }

    public void Remove(string site) => Write("DELETE FROM keep_warm WHERE site = $site", site);

    private List<KeepWarmRecord> Read(string sql, params (string, object?)[] parameters)
    {
        try
        {
            using var connection = _database.Open();
            using var command = AppDatabase.Command(connection, sql, parameters);
            using var reader = command.ExecuteReader();
            var records = new List<KeepWarmRecord>();
            while (reader.Read()) records.Add(new KeepWarmRecord(reader.GetString(0), Enabled: true));
            return records;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the sites kept warm");
            return [];
        }
    }

    private void Write(string sql, string site)
    {
        try
        {
            using var connection = _database.Open();
            AppDatabase.Execute(connection, sql, ("$site", site));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save whether {Site} is kept warm", site);
        }
    }
}
