using Microsoft.Win32;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// The LocalDB versions on this PC, and which database file versions each opens. LocalDB isn't one server: every Windows
/// account has its own <c>MSSQLLocalDB</c>, at the version it was made with. With several LocalDB versions installed
/// (Visual Studio brings 2019, SQL Server Express setup the newest) the user's instance and a site's app pool identity's
/// can differ - and a file one of them made, the other may not open.
/// </summary>
public static class LocalDbVersions
{
    // The newest database file version each LocalDB major opens - and gives a file it opens: attaching upgrades it.
    private static readonly (int Major, int FileVersion)[] FileVersions =
        [(11, 706), (12, 782), (13, 852), (14, 869), (15, 904), (16, 957), (17, 998)];

    // Where a database file keeps its version: dbi_version on its boot page (page 9, after the 96-byte page header).
    private const long VersionOffset = 0x12064;

    /// <summary>The installed LocalDB majors (15 = 2019, 17 = 2025), each with its instance API DLL - from the registry.</summary>
    public static IReadOnlyDictionary<int, string> Installed()
    {
        var installed = new Dictionary<int, string>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");
        if (key is null) return installed;
        foreach (var name in key.GetSubKeyNames())
        {
            if (!int.TryParse(name.Split('.')[0], out var major) || major <= 0) continue;
            using var version = key.OpenSubKey(name);
            installed[major] = version?.GetValue("InstanceAPIPath") as string ?? "";
        }
        return installed;
    }

    /// <summary>The newest file version <paramref name="major"/> opens - any for a LocalDB newer than this list.</summary>
    public static int MaxFileVersion(int major) =>
        major > FileVersions[^1].Major ? int.MaxValue : FileVersions.FirstOrDefault(v => v.Major == major).FileVersion;

    /// <summary>The LocalDB major that makes files of <paramref name="fileVersion"/> - the oldest that opens it (998 → 17).</summary>
    public static int MajorFor(int fileVersion) =>
        FileVersions.FirstOrDefault(v => v.FileVersion >= fileVersion).Major is var major and > 0 ? major : FileVersions[^1].Major + 1;

    /// <summary>
    /// The LocalDB to open a file of <paramref name="fileVersion"/> in, of those <paramref name="installed"/>: the oldest
    /// that opens it - the file's own version when that is installed. An older one can't open it; a newer one would
    /// upgrade it for good, and the instance that made it could no longer open it. Null when none opens it.
    /// </summary>
    public static int? MajorToOpen(int fileVersion, IEnumerable<int> installed) =>
        installed.Where(major => MaxFileVersion(major) >= fileVersion).Order().Cast<int?>().FirstOrDefault();

    /// <summary>
    /// A database file's version (611 = SQL Server 2005, 998 = 2025) from its header; null when the file is too short for
    /// one. Throws <see cref="IOException"/> while SQL Server holds the file.
    /// </summary>
    public static int? FileVersionOf(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < VersionOffset + 2) return null;
        stream.Seek(VersionOffset, SeekOrigin.Begin);
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        var version = BitConverter.ToInt16(bytes);
        return version > 0 ? version : null;
    }

    /// <summary>SqlLocalDB.exe of the LocalDB whose instance API is <paramref name="instanceApi"/> - or the one on PATH.</summary>
    public static string SqlLocalDbExe(string instanceApi)
    {
        // ...\170\LocalDB\Binn\SqlUserInstance.dll → ...\170\Tools\Binn\SqlLocalDB.exe
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(instanceApi)));
        var exe = root is null ? null : Path.Combine(root, "Tools", "Binn", "SqlLocalDB.exe");
        return exe is not null && File.Exists(exe) ? exe : "SqlLocalDB.exe";
    }
}
