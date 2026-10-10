using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DnnManager.Application;

/// <summary>
/// Paths in folders others can write to - a site's folder (its app pool can), the projects folder, Documents - for what
/// DNN Manager does there with its administrator rights: delete, write, copy, change permissions. A link or junction
/// on the way would take that to wherever it points (a Program Files folder, System32). The process doesn't follow
/// junctions made without administrator rights at all (Infrastructure's RedirectionTrust); this is the check in the
/// code itself, also for a Windows without that, and for symbolic links. The one place that decides whether a path is
/// in a folder - nothing else compares paths with StartsWith.
/// </summary>
public static class SafePath
{
    /// <summary>
    /// The line an error about a link or junction ends with: what to do about it. The Output tab shows a line starting
    /// "Hint:" as the step's "→".
    /// </summary>
    public const string LinkHint =
        "Hint: if the link is meant to be there, recreate it from an administrator command prompt (mklink /J) - or copy the files in instead of linking them.";

    /// <summary>Enumerating a tree without going into links or junctions.</summary>
    public static EnumerationOptions Recursive => new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Enumerating one folder, without its links or junctions.</summary>
    public static EnumerationOptions TopLevel => new() { AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="folder"/> itself or inside it - both made full first, so
    /// <c>a\..\b</c> and a trailing separator don't matter. Works when the folder is a drive (<c>C:\</c>). False when either
    /// isn't a valid path.
    /// </summary>
    public static bool IsSameOrInside(string path, string folder) =>
        Full(path) is { } p && Full(folder) is { } f && SameOrInside(p, f);

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> - not the folder itself.</summary>
    public static bool IsInside(string path, string folder) =>
        Full(path) is { } p && Full(folder) is { } f && SameOrInside(p, f) && !p.Equals(f, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> - or a folder on the way to it from <paramref name="root"/>, the root included -
    /// is a link or junction. What doesn't exist yet isn't one; a path outside the root counts as one (never trusted).
    /// <paramref name="checkedFolders"/>, when given, remembers the folders already found to be no link - for an
    /// extraction that asks for thousands of files in the same few folders.
    /// </summary>
    public static bool HasLink(string root, string path, ISet<string>? checkedFolders = null)
    {
        if (Full(root) is not { } top || Full(path) is not { } at || !SameOrInside(at, top)) return true;
        var walked = new List<string>();
        while (true)
        {
            if (checkedFolders is not null && checkedFolders.Contains(at)) break;
            var (exists, link) = State(at);
            if (link) return true;
            // Only a folder that is there is remembered: one made later could be made as a junction.
            if (exists) walked.Add(at);
            if (at.Equals(top, StringComparison.OrdinalIgnoreCase)) break;
            at = Path.GetDirectoryName(at) ?? top;
        }
        // Remembered only once the whole way is known to be clear.
        if (checkedFolders is not null) foreach (var folder in walked) checkedFolders.Add(folder);
        return false;
    }

    /// <summary>
    /// <paramref name="relative"/> (a zip entry's name, say) under <paramref name="root"/> - or null when it would land
    /// outside it ("..", a full path, a drive, a stream) or on the root itself ("./", "a/..", "..."), or go through a link
    /// or junction. What comes back is strictly inside the root.
    /// </summary>
    public static string? Under(string root, string relative, ISet<string>? checkedFolders = null)
    {
        if (relative.Length == 0 || relative.Contains(':') || relative.Contains('\0') || Path.IsPathRooted(relative)) return null;
        if (Full(root) is not { } top) return null;
        string target;
        try { target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(top, relative))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!SameOrInside(target, top) || target.Equals(top, StringComparison.OrdinalIgnoreCase)) return null;
        return HasLink(top, target, checkedFolders) ? null : target;
    }

    /// <summary>Throws when <see cref="HasLink"/> - for a folder about to be emptied, written to or deleted.</summary>
    public static void EnsureNoLink(string root, string path, ISet<string>? checkedFolders = null)
    {
        if (HasLink(root, path, checkedFolders))
            throw new IOException($"{path} is reached through a link or junction - DNN Manager doesn't delete or write through those, " +
                                  $"with its administrator rights that could reach a folder outside the site.{Environment.NewLine}{LinkHint}");
    }

    /// <summary>
    /// Whether <paramref name="path"/> exists and is a link or junction itself - one read of its attributes. One that can't
    /// be read isn't trusted either.
    /// </summary>
    public static bool IsLink(string path) => State(path).Link;

    /// <summary>Whether <paramref name="path"/> is there, and whether it is a link - one read of its attributes.</summary>
    private static (bool Exists, bool Link) State(string path)
    {
        try
        {
            var attributes = new FileInfo(path).Attributes;
            // -1: not there.
            return (int)attributes == -1 ? (false, false) : (true, attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Can't be read: not to be trusted either.
            return (true, true);
        }
    }

    /// <summary>
    /// Whether the file <paramref name="path"/> has other names (hard links) elsewhere - writing into it in place, or
    /// changing its rights, would change that other file too (a hard link the app pool made to a file only an administrator
    /// may change). Such a file is replaced, not written into. One that can't be read isn't trusted; one that isn't there
    /// has no other name.
    /// </summary>
    public static bool IsHardLinked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            // No access to its data needed - only its information; not following a link at the end.
            using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return true;
            return !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks > 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Deletes the folder <paramref name="path"/> with everything in it, never following a link or junction in it - a link
    /// is deleted itself, what it points to stays. Read-only files are deleted too. Throws what the last failure was.
    /// </summary>
    public static void DeleteTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Only the link: Directory.Delete without recursion removes a junction, not its target.
            dir.Delete();
            return;
        }
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry is DirectoryInfo sub) DeleteTree(sub.FullName);
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        if (dir.Attributes.HasFlag(FileAttributes.ReadOnly)) dir.Attributes &= ~FileAttributes.ReadOnly;
        dir.Delete();
    }

    /// <summary>
    /// <paramref name="path"/> with every part that exists spelled as on disk - a short name (<c>WEB~1.CON</c>) as its
    /// long one (<c>web.config</c>) - so two spellings of one file compare equal. The parts that don't exist yet stay as
    /// they are (no short name can point at what isn't there).
    /// </summary>
    public static string LongPath(string path)
    {
        if (Full(path) is not { } full) return path;
        var existing = full;
        var rest = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return full;
            rest.Push(Path.GetFileName(existing));
            existing = parent;
        }
        var buffer = new char[32768];
        var length = GetLongPathName(existing, buffer, (uint)buffer.Length);
        var resolved = length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : existing;
        while (rest.Count > 0) resolved = Path.Combine(resolved, rest.Pop());
        return resolved;
    }

    /// <summary>A full path without its trailing separator (a drive keeps its own: <c>C:\</c>); null when it isn't valid.</summary>
    private static string? Full(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0')) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool SameOrInside(string path, string folder)
    {
        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
        // A drive ("C:\") already ends with its separator.
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Win32 ────────────────────────────────────────────────────────────

    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint length);
}
