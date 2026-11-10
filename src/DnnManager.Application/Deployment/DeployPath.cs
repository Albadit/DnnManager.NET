namespace DnnManager.Application.Deployment;

/// <summary>
/// The paths of a deployment: every file is named relative to the project's folder and the server's folder alike, with
/// <c>/</c> between folders (<c>Portals/0/site.css</c>). A server's listing is untrusted - a name that could leave the
/// folder (<c>..</c>, a full path, a drive) or that Windows can't hold is refused, never followed.
/// </summary>
public static class DeployPath
{
    // What Windows doesn't allow in a name - a server's file named so can't be pulled, and is left out of a comparison.
    private static readonly char[] Invalid = ['\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// <paramref name="path"/> as a relative deployment path - <c>\</c> made <c>/</c>, no leading or doubled <c>/</c> - or
    /// null when it isn't a safe one: empty, rooted, with a <c>.</c> or <c>..</c> part, a control character, a name Windows
    /// can't hold (<c>a:b</c>, <c>CON</c>, a trailing dot or space).
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || path.StartsWith('/') || path.StartsWith('\\')) return null;
        foreach (var part in parts)
            if (!IsValidName(part)) return null;
        return string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="name"/> is one name (no <c>/</c>) that is safe here and on Windows.</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Length > 255) return false;
        if (name.Any(c => char.IsControl(c) || c == '/' || Invalid.Contains(c))) return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0];
        return !Reserved.Contains(stem);
    }

    /// <summary>
    /// A server folder as given by the user - <c>/var/www/shop</c>, <c>httpdocs</c>, <c>/</c> - with <c>/</c> between
    /// parts and none at the end; null when it has a <c>..</c> part or a control character.
    /// </summary>
    public static string? NormalizeRemoteRoot(string? root)
    {
        var value = (root ?? "").Trim().Replace('\\', '/');
        if (value.Length == 0) return null;
        if (value.Any(char.IsControl)) return null;
        var rooted = value.StartsWith('/');
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) return null;
        var joined = string.Join('/', parts);
        return rooted ? "/" + joined : joined.Length == 0 ? null : joined;
    }

    /// <summary>The server path of <paramref name="relative"/> under <paramref name="root"/> - both already normalized.</summary>
    public static string Remote(string root, string relative) =>
        root == "/" ? "/" + relative : root.TrimEnd('/') + "/" + relative;

    /// <summary>
    /// The relative path of <paramref name="full"/>, a server path under <paramref name="root"/> - null when it isn't under
    /// it or isn't a safe path.
    /// </summary>
    public static string? RelativeTo(string root, string full)
    {
        var prefix = root == "/" ? "/" : root.TrimEnd('/') + "/";
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return Normalize(full[prefix.Length..]);
    }

    /// <summary>The folder of a relative path - "" for one at the top.</summary>
    public static string Folder(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }

    /// <summary>The file name of a relative path.</summary>
    public static string Name(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    /// <summary>The relative path as a Windows path under <paramref name="root"/> - through <see cref="SafePath.Under"/>: null when it would leave the root or go through a link.</summary>
    public static string? Local(string root, string relative) =>
        Normalize(relative) is { } safe ? SafePath.Under(root, safe.Replace('/', Path.DirectorySeparatorChar)) : null;

    /// <summary>Paths compare as Windows compares names - without case - since the project's folder is on Windows.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The names DNN Manager itself leaves on a server while it works - the lock, a file half sent - never part of a
    /// comparison.
    /// </summary>
    public static bool IsOwnFile(string relative)
    {
        var name = Name(relative);
        return name.Equals(LockFileName, StringComparison.OrdinalIgnoreCase) || name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The lock a push, pull or rollback holds on the server's folder while it runs.</summary>
    public const string LockFileName = ".dnnmanager.lock";

    /// <summary>A file being sent, before it takes the real one's place.</summary>
    public const string PartSuffix = ".dnnmgr-part";

    /// <summary>The name a file is sent under before it replaces <paramref name="relative"/> - beside it, so the move is a rename.</summary>
    public static string PartName(string relative, string hash)
    {
        var folder = Folder(relative);
        // A name stays within the 255 characters a server allows.
        var original = Name(relative);
        var name = $".{original[..Math.Min(200, original.Length)]}.{hash[..Math.Min(12, hash.Length)]}{PartSuffix}";
        return folder.Length == 0 ? name : folder + "/" + name;
    }
}
