namespace DnnManager.Domain;

public sealed record DnnProject(
    string Name,
    string ProjectDirectory,
    string BackupDirectory);

public sealed record DnnRelease(string Version, string TagName, string DownloadUrl, bool Prerelease = false);

public sealed record DatabaseConfig(
    string Server,
    string DatabaseName,
    string Collation,
    int Port,
    string BackupDirectory);

