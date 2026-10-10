using System.IO.Compression;
using DnnManager.Application;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Unpacking a zip someone else made - a backup, a DNN package - into a folder others can write to, with DNN Manager's
/// administrator rights. Every extraction loop goes through <see cref="ExtractEntry"/>: an entry lands strictly inside
/// the folder or not at all - never on another file's stream (<c>web.config::$DATA</c>), never through a link or junction,
/// never into a file that has other names (hard links) - and <see cref="EnsureFits(IEnumerable{ZipArchiveEntry}, string)"/> refuses a zip that would fill
/// the disk (a zip bomb) before anything is written.
/// </summary>
public static class SafeZip
{
    /// <summary>What is left free on the disk after unpacking, at least.</summary>
    internal const long Headroom = 512L * 1024 * 1024;

    /// <summary>An entry's name with '/' between its folders - some zip tools write '\'.</summary>
    public static string Name(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    /// <summary>A folder entry: its name ends with a separator, and it holds nothing.</summary>
    public static bool IsFolder(ZipArchiveEntry entry) => Name(entry).EndsWith('/');

    /// <summary>
    /// Where <paramref name="relativeName"/> (an entry's name, or the part of it under the site's root in the zip) lands
    /// under <paramref name="root"/>, every part spelled as on disk (a short name like <c>WEB~1.CON</c> as the long one it
    /// stands for) - or an exception saying why it may not: outside the folder, the folder itself, a stream (':'), or
    /// through a link or junction.
    /// </summary>
    public static string Target(string root, string relativeName, ISet<string>? checkedFolders = null)
    {
        if (relativeName.Contains(':'))
            throw new IOException($"The zip contains an unsafe path: {relativeName} (a ':' would write into another file's stream).");
        if (SafePath.Under(root, relativeName, checkedFolders) is not { } target)
            throw new IOException(ThroughLink(root, relativeName)
                ? $"The folder has a link or junction on the way to {relativeName} - DNN Manager doesn't write through those.{Environment.NewLine}{SafePath.LinkHint}"
                : $"The zip contains an unsafe path: {relativeName}");
        return SafePath.LongPath(target);
    }

    // Refused by Under although it stays inside the folder: a link or junction on the way.
    private static bool ThroughLink(string root, string relativeName)
    {
        try
        {
            if (Path.IsPathRooted(relativeName)) return false;
            var path = Path.GetFullPath(Path.Combine(root, relativeName));
            return SafePath.IsInside(path, root) && SafePath.HasLink(root, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path of <paramref name="target"/> relative to <paramref name="root"/>, with '/' between its folders - to compare
    /// with a list of names (DNN's upgradeExclude) after "./", "a/../" and short names are resolved.
    /// </summary>
    public static string Relative(string root, string target) =>
        Path.GetRelativePath(SafePath.LongPath(root), target).Replace('\\', '/');

    /// <summary>
    /// Writes <paramref name="entry"/> to <paramref name="target"/> (from <see cref="Target"/>): its folder made once
    /// it is clear of links, a link in the file's place refused, a file with other names replaced rather than written
    /// into, a read-only one overwritten.
    /// </summary>
    public static void Write(string root, ZipArchiveEntry entry, string target, ISet<string> checkedFolders)
    {
        var folder = Path.GetDirectoryName(target)!;
        if (!checkedFolders.Contains(folder))
        {
            SafePath.EnsureNoLink(root, folder, checkedFolders);
            Directory.CreateDirectory(folder);
            // Made now (only folders that are there are remembered): checked again, so a junction put in its place in
            // between isn't written through.
            SafePath.EnsureNoLink(root, folder, checkedFolders);
        }
        if (SafePath.IsLink(target))
            throw new IOException($"{target} is a link - DNN Manager doesn't write through those.{Environment.NewLine}{SafePath.LinkHint}");
        if (File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
            // Another name of the same file elsewhere would be overwritten too: this name is taken away, and a new file made.
            if (SafePath.IsHardLinked(target)) File.Delete(target);
        }
        entry.ExtractToFile(target, overwrite: true);
    }

    /// <summary>
    /// <see cref="Target"/> and <see cref="Write"/> in one: <paramref name="entry"/> under <paramref name="root"/> as
    /// <paramref name="relativeName"/> (its own name when null). Returns where it was written.
    /// </summary>
    public static string ExtractEntry(string root, ZipArchiveEntry entry, ISet<string> checkedFolders, string? relativeName = null)
    {
        var target = Target(root, relativeName ?? Name(entry), checkedFolders);
        Write(root, entry, target, checkedFolders);
        return target;
    }

    /// <summary>
    /// Throws when <paramref name="entries"/> unpacked would leave less than <see cref="Headroom"/> free on the disk of
    /// <paramref name="root"/> - a zip bomb (a few KB that unpack to terabytes), or a package too big for the disk, fails
    /// before the first file instead of filling it. The sizes are those the zip says; .NET's unpacking refuses an entry
    /// that holds more than its size says.
    /// </summary>
    public static void EnsureFits(IEnumerable<ZipArchiveEntry> entries, string root) =>
        EnsureFits(entries.Sum(e => Math.Max(0, e.Length)), FreeSpace(root), root);

    internal static void EnsureFits(long total, long? free, string root)
    {
        if (free is not { } available) return;
        if (total > available - Headroom)
            throw new IOException($"The zip unpacks to {total / 1048576d:N0} MB, but the disk of {root} has {available / 1048576d:N0} MB free - " +
                                  $"it isn't unpacked (at least {Headroom / 1048576} MB are kept free).");
    }

    private static long? FreeSpace(string root)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            return string.IsNullOrEmpty(drive) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
