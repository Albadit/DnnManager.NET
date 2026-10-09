using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IRemoteSqlBackupService
{
    /// <summary>
    /// Issues BACKUP DATABASE against the given (possibly external) SQL Server using SQL auth.
    /// Returns the host-side path of the resulting <c>.bak</c> when it can be read by this process.
    /// The caller supplies <paramref name="backupServerPath"/> - a path the SQL Server service can write to.
    /// For sources whose Data Source resolves to this machine that's a normal local path; for remote
    /// servers it must be a UNC share readable from here.
    /// </summary>
    Task<Result<string>> BackupAsync(SiteSqlConnection source, string backupServerPath, IProgressReporter reporter, CancellationToken ct);
}
