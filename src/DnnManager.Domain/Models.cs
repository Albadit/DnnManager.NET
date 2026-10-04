namespace DnnManager.Domain;

public sealed record DnnProject(
    string Name,
    string ProjectDirectory,
    string BackupDirectory);

public sealed record DnnRelease(string Version, string TagName, string DownloadUrl, bool Prerelease = false);

/// <summary>
/// A repository's DNN releases. <paramref name="SavedAt"/>: GitHub couldn't be reached, so these are the releases
/// saved at the last lookup, at that time; null when they come from GitHub now.
/// </summary>
public sealed record DnnReleaseList(IReadOnlyList<DnnRelease> Releases, DateTime? SavedAt = null);

public sealed record DatabaseConfig(
    string Server,
    string DatabaseName,
    string Collation,
    int Port,
    string BackupDirectory);

