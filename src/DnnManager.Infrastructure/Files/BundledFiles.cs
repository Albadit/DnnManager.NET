namespace DnnManager.Infrastructure.Files;

/// <summary>
/// The default <c>appsettings.json</c> and <c>docker-compose.yml</c> are compiled into the app as
/// resources, so a copy missing from next to the exe (a partial copy, a cleaned publish folder…) is
/// recreated instead of the app failing to start or to bring up SQL Server.
/// </summary>
public static class BundledFiles
{
    public const string AppSettings = "appsettings.json";
    public const string DockerCompose = "docker-compose.yml";

    /// <summary>
    /// Writes the bundled default of <paramref name="fileName"/> next to the app when it is missing.
    /// Returns true when the file was created; an existing file is never touched.
    /// </summary>
    public static bool EnsureExists(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(path)) return false;

        using var resource = typeof(BundledFiles).Assembly.GetManifestResourceStream("defaults/" + fileName)
            ?? throw new InvalidOperationException($"No bundled default for {fileName}.");

        // Write beside it and move into place, so a crash never leaves a half-written file behind.
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
            resource.CopyTo(file);
        File.Move(tmp, path, overwrite: false);
        return true;
    }
}
