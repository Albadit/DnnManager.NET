namespace DnnManager.Domain;

public sealed record DnnProject(
    string Name,
    string ProjectDirectory,
    string BackupDirectory);

public sealed record DnnRelease(string Version, string TagName, string DownloadUrl);

public sealed record DatabaseConfig(
    string Server,
    string DatabaseName,
    string Collation,
    int Port,
    string BackupDirectory);

public sealed record ProjectStatus(
    string Name,
    string ProjectDirectory,
    bool IisSiteExists,
    string? IisSiteState,
    long DirectorySizeBytes,
    bool ContainerRunning,
    string? DatabaseName,
    int? SqlPort,
    string SiteUrl);
