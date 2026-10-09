using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IDnnReleaseService
{
    /// <summary>The release of that version, or the latest release - offline, from the releases saved at the last lookup.</summary>
    Task<Result<DnnRelease>> GetReleaseAsync(string apiUrl, string? version, CancellationToken ct);

    /// <summary>
    /// The releases of <paramref name="apiUrl"/> that have a DNN install package, pre-releases included (no drafts),
    /// highest version first - a release before a pre-release of the same version. When GitHub can't be reached, the
    /// releases saved at the last lookup (<see cref="DnnReleaseList.SavedAt"/>), if any.
    /// </summary>
    Task<Result<DnnReleaseList>> ListReleasesAsync(string apiUrl, CancellationToken ct);

    IReadOnlyList<string> KnownReleaseApis { get; }
}
