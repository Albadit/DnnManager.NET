namespace DnnManager.Domain;

/// <summary>
/// Validation for a managed project's name.
/// </summary>
/// <remarks>
/// The name is used verbatim in four places, so it has to satisfy all of them at once:
/// a single directory segment under the base directory, an IIS site + application-pool name,
/// a DNS host label (<c>{name}.{suffix}</c>) and the prefix of a SQL database name. Most
/// importantly it must stay a <em>single relative segment</em>: anything containing a separator,
/// a drive qualifier or <c>..</c> would escape the base directory - and removing a project
/// recursively deletes that resolved path.
/// </remarks>
public static class ProjectName
{
    public const int MaxLength = 48;

    // Windows refuses to create files/directories with these names (with or without an extension),
    // so a project named after one would fail late and confusingly.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static Result Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Fail("Project name is required.");

        if (name.Length > MaxLength)
            return Result.Fail($"Project name is too long (max {MaxLength} characters).");

        if (!char.IsAsciiLetterOrDigit(name[0]))
            return Result.Fail("Project name must start with a letter or a digit.");

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return Result.Fail(
                    $"Project name contains an unsupported character '{c}'. Use letters, digits, '-', '_' or '.' only " +
                    "(the name becomes a folder, an IIS site and a hostname).");
        }

        // '..' would climb out of the base directory; a trailing dot is silently stripped by Windows,
        // which would make the created folder and the name we manage disagree.
        if (name.Contains("..", StringComparison.Ordinal))
            return Result.Fail("Project name cannot contain '..'.");
        if (name.EndsWith('.'))
            return Result.Fail("Project name cannot end with '.'.");

        // A device name is reserved on its own and with any extension (e.g. "con" and "con.dev").
        var stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
            return Result.Fail($"'{name}' uses the reserved Windows device name '{stem}'.");

        return Result.Ok();
    }
}
