namespace DnnManager.Infrastructure.Docker;

/// <summary>
/// The container's and the volume's names from the settings, checked before they go on a docker command line or into
/// the compose file: a name starting with <c>-</c> would be read as an option (<c>docker exec --privileged …</c>), one
/// with a path character as a folder (<c>/</c> makes a volume a bind mount of the host's). The settings check them too -
/// this is the check where they are used.
/// </summary>
public static class DockerNames
{
    /// <summary>What Docker itself allows for a container or a volume: a letter or digit, then letters, digits, <c>_ . -</c>.</summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>Why <paramref name="name"/> (the <paramref name="what"/>'s, e.g. "container") can't be used - or null when it can.</summary>
    public static string? Refusal(string? name, string what) =>
        IsValid(name)
            ? null
            : $"'{name}' can't be the {what}'s name - only letters, digits, '_', '.' and '-', starting with a letter or digit " +
              "(Settings → Docker container).";
}
