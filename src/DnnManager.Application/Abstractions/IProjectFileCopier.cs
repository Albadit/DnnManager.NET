using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IProjectFileCopier
{
    /// <summary>Copies a DNN project's website files from <paramref name="sourceDirectory"/> into <paramref name="destinationDirectory"/>.</summary>
    Task<Result> CopyAsync(string sourceDirectory, string destinationDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Extracts a zipped DNN site into <paramref name="destinationDirectory"/>. The site root is the zip's
    /// shallowest folder holding a <c>web.config</c>, so a zip with everything under one top folder works too.
    /// </summary>
    Task<Result> ExtractZipAsync(string zipPath, string destinationDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Zips every file under <paramref name="sourceDirectory"/> into <paramref name="zipPath"/>, leaving out
    /// <paramref name="excludedPaths"/> - files or folders (with everything in them), relative to
    /// <paramref name="sourceDirectory"/>, e.g. <c>.git</c> or <c>App_Data\Search</c>. Files that can't be read are
    /// skipped and reported.
    /// </summary>
    Task<Result> CreateZipAsync(string sourceDirectory, string zipPath, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Deletes the files in <paramref name="directory"/> that the site zip <paramref name="zipPath"/> doesn't hold - what
    /// was added since it was made - apart from <paramref name="excludedPaths"/> (as for <see cref="CreateZipAsync"/>: what
    /// the zip never held). After <see cref="ExtractZipAsync"/>, the folder is as the zip has it. The number deleted.
    /// </summary>
    Task<Result<int>> RemoveFilesNotInZipAsync(string zipPath, string directory, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct);
}
