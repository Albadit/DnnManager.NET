using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IDnnPackageInstaller
{
    Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>The release's install package is kept from an earlier download, so installing it needs no download.</summary>
    bool IsKept(DnnRelease release);

    /// <summary>
    /// Puts the release's upgrade package (<see cref="DnnRelease.UpgradeUrl"/>) over the site in
    /// <paramref name="projectDirectory"/> - every file of it but <c>web.config</c>, which keeps the site's own settings.
    /// Kept like an install package (Keep downloaded DNN install packages), otherwise downloaded and deleted again.
    /// </summary>
    Task<Result> ExtractUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// For a site on DNN 10.2 or newer: puts the release's <b>install</b> package over the site as DNN's own local upgrade
    /// does (DNN's <c>LocalUpgradeService.StartLocalUpgrade</c>) - each assembly in bin with a binding redirect to its
    /// version in web.config, everything else but what the package's <c>App_Data/Upgrade/upgrade.json</c> excludes
    /// (web.config among it). Unzipping the upgrade package over a 10.2+ site instead leaves web.config's binding redirects
    /// behind its new assemblies, and the site doesn't start.
    /// </summary>
    Task<Result> ExtractLocalUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct);
}
