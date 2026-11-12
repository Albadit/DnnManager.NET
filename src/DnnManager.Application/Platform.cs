namespace DnnManager.Application;

/// <summary>What DNN Manager does that only one operating system has.</summary>
public enum PlatformFeature
{
    /// <summary>IIS - the sites, app pools and bindings; DNN itself runs on .NET Framework in IIS.</summary>
    Iis,
    /// <summary>Making a DNN site: IIS, SQL Server Express LocalDB, the hosts file.</summary>
    Setup,
    /// <summary>SQL Server Express LocalDB.</summary>
    LocalDb,
    /// <summary>The hosts file kept with the sites' host names.</summary>
    HostsFile,
    /// <summary>Keeping sites warm, with IIS's app pools.</summary>
    KeepWarm,
    /// <summary>Starting with Windows (a scheduled task).</summary>
    StartWithSystem,
}

/// <summary>
/// The operating system DNN Manager runs on, and what it can do there. DNN runs on .NET Framework 4.8 in IIS - Windows
/// only - so on macOS and Linux everything that makes or runs a site is left out, and Source Control (comparing,
/// pushing and pulling a project's files over FTP and SFTP) is there as on Windows.
/// </summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>The name the user knows the system by - "Windows", "macOS", "Linux".</summary>
    public static string Name =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "this system";

    /// <summary>Whether <paramref name="feature"/> works here.</summary>
    public static bool Supports(PlatformFeature feature) => IsWindows;

    /// <summary>Why <paramref name="feature"/> isn't there on this system - null when it is.</summary>
    public static string? WhyNot(PlatformFeature feature) => Supports(feature)
        ? null
        : feature switch
        {
            PlatformFeature.Iis or PlatformFeature.Setup or PlatformFeature.KeepWarm =>
                $"DNN runs on .NET Framework in IIS, which only Windows has - on {Name}, run DNN Manager in a Windows virtual " +
                "machine (Parallels, UTM, VirtualBox) to make and run sites. Source Control works here.",
            PlatformFeature.LocalDb => $"SQL Server Express LocalDB is only made for Windows - there is none for {Name}.",
            PlatformFeature.HostsFile => "DNN Manager keeps the hosts file only for the sites it runs in IIS, on Windows.",
            PlatformFeature.StartWithSystem => $"Starting with the system is only there on Windows for now.",
            _ => $"Not available on {Name}.",
        };

    /// <summary>The projects folder a new install starts with: <c>C:\DNN</c> on Windows, <c>~/DNN</c> elsewhere.</summary>
    public static string DefaultProjectsFolder =>
        IsWindows ? @"C:\DNN" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DNN");

    /// <summary>A projects folder as an example in what the settings say - <c>C:\DNN</c>, or <c>~/DNN</c> written out.</summary>
    public static string ExampleProjectsFolder => DefaultProjectsFolder;

    /// <summary>
    /// The folders of macOS and Linux that are the system's own - never a projects folder, nor a folder in one. The
    /// user's home is checked apart (SettingRules).
    /// </summary>
    public static IReadOnlyList<string> UnixSystemFolders { get; } =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/usr", "/var",
        "/Applications", "/Library", "/System", "/private", "/cores"
    ];
}
