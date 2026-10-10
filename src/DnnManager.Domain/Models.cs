namespace DnnManager.Domain;

public sealed record DnnProject(
    string Name,
    string ProjectDirectory,
    string BackupDirectory);

/// <param name="Sha256">GitHub's SHA-256 of the install package (lowercase hex), when GitHub gives one - older releases have none.</param>
/// <param name="UpgradeSha256">The same for the upgrade package (<see cref="UpgradeUrl"/>).</param>
public sealed record DnnRelease(string Version, string TagName, string DownloadUrl, bool Prerelease = false, string? Sha256 = null,
    string? UpgradeSha256 = null)
{
    /// <summary>
    /// The release's upgrade package - DNN publishes <c>DNN_Platform_X_Upgrade.zip</c> beside the install package
    /// (<see cref="DownloadUrl"/>, <c>…_Install.zip</c>); null when the install package isn't named that way.
    /// </summary>
    public string? UpgradeUrl =>
        DownloadUrl.EndsWith("_Install.zip", StringComparison.OrdinalIgnoreCase) ? DownloadUrl[..^"_Install.zip".Length] + "_Upgrade.zip" : null;
}

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

