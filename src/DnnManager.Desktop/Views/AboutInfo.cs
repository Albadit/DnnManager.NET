using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Diagnostics;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;

namespace DnnManager.Desktop.Views;

/// <summary>How a value in About is doing - the dot beside it.</summary>
internal enum AboutHealth { None, Ok, Warning, Bad, Unknown }

/// <summary>A value in About: its name, the value, the dot and a note under it.</summary>
internal sealed record AboutRow(string Name, string Value, AboutHealth Health = AboutHealth.None, string? Note = null);

/// <summary>A section of About: "DNN Manager - the program" and its values.</summary>
internal sealed class AboutSection(string title, string subtitle)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public List<AboutRow> Rows { get; } = [];

    public void Add(string name, string? value, AboutHealth health = AboutHealth.None, string? note = null)
    {
        if (value is not null) Rows.Add(new AboutRow(name, value, health, note));
    }
}

/// <summary>What GitHub says about a newer DNN Manager - asked once while Settings is open.</summary>
internal sealed record UpdateInfo(string Text, AboutHealth Health, string? Note = null);

/// <summary>
/// Settings → About: which DNN Manager this is, what it runs on and what it works with - each read from where it is (the
/// program, the system), the update status from GitHub's releases. The Windows app's Pages/AboutInfo, for every system.
/// </summary>
internal static class AboutInfo
{
    public const string Repository = AppReleaseFeed.Repository;

    /// <summary>The version this program says it is - "2.0.0-rc1+abc" - or "" for a build without one.</summary>
    public static string Informational =>
        typeof(AboutInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public static IEnumerable<AboutSection> Sections(UpdateInfo update, string? docker)
    {
        var assembly = typeof(AboutInfo).Assembly;
        var version = assembly.GetName().Version;
        var informational = Informational;
        var commit = informational.Split('+', 2) is [_, var sha] && sha.Length > 0 ? sha[..Math.Min(12, sha.Length)] : null;
        var debug = assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITTrackingEnabled == true;
        var exe = Environment.ProcessPath;

        var app = new AboutSection("DNN Manager", "the program");
        app.Add("Version", informational.Split('+')[0] is { Length: > 0 } shown ? shown
            : version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}");
        app.Add("Build", commit is null ? null : $"commit {commit}");
        if (exe is not null && File.Exists(exe)) app.Add("Built", File.GetLastWriteTime(exe).ToString("g"));
        app.Add("Program folder", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        app.Add("Release channel", debug ? "development (Debug build)" : informational.Split('+')[0].Contains('-') ? "pre-release" : "stable");
        app.Add("Update", update.Text, update.Health, update.Note);
        app.Add("License", "MIT");
        app.Add("Repository", Repository);
        app.Add("Documentation", $"{Repository}/tree/main/.docs");
        yield return app;

        var runtime = new AboutSection("Runs on", "this computer");
        runtime.Add(".NET", RuntimeInformation.FrameworkDescription);
        runtime.Add("Architecture", $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} process on " +
                                    $"{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()} {Platform.Name}");
        runtime.Add(Platform.Name, OperatingSystem.IsWindows() ? MachineInfo.Windows : RuntimeInformation.OSDescription);
        if (OperatingSystem.IsWindows())
        {
            var admin = IsAdministrator();
            runtime.Add("Administrator", admin ? "yes - it manages IIS" : "no", admin ? AboutHealth.None : AboutHealth.Bad);
        }
        if (UserCipher.KeyFile is { } key)
            runtime.Add("Password key", key, note: "Your saved passwords are encrypted with it - only you can read it. Lose it and they have to be added again.");
        yield return runtime;

        var components = new AboutSection("Works with", OperatingSystem.IsWindows() ? "Windows and its own libraries" : "its own libraries");
        if (OperatingSystem.IsWindows())
        {
            components.Add("IIS", MachineInfo.Iis ?? "not installed", MachineInfo.Iis is null ? AboutHealth.Bad : AboutHealth.None);
            components.Add(".NET Framework (sites)", MachineInfo.NetFramework ?? "not found", MachineInfo.NetFramework is null ? AboutHealth.Bad : AboutHealth.None);
        }
        else components.Add("IIS", $"Windows only", AboutHealth.None, Platform.WhyNot(PlatformFeature.Iis));
        components.Add("Docker", docker ?? "checking…");
        if (OperatingSystem.IsWindows())
            components.Add("Microsoft.Web.Administration", LibraryVersion(typeof(Microsoft.Web.Administration.ServerManager)));
        components.Add("Microsoft.Data.SqlClient", LibraryVersion(typeof(Microsoft.Data.SqlClient.SqlConnection)));
        components.Add("Avalonia", LibraryVersion(typeof(Avalonia.Application)));
        yield return components;
    }

    private static string LibraryVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? type.Assembly.GetName().Version?.ToString() ?? "unknown";

    private static bool IsAdministrator() => Services.AdminElevation.IsAdministrator();

    /// <summary>
    /// The update status, from GitHub's latest release (never a pre-release): up to date, a newer one (and where to get it),
    /// or why GitHub couldn't be asked.
    /// </summary>
    public static async Task<UpdateInfo> UpdateAsync()
    {
        var running = Informational;
        if (!AppReleaseFeed.TryParseVersion(running, out var current) || current.Major == 0)
            return new UpdateInfo("a build without a version - not checked", AboutHealth.None);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var latest = await new AppReleaseFeed(http).GetLatestAsync(CancellationToken.None);
            return AppReleaseFeed.IsNewer(latest.Version, current)
                ? new UpdateInfo($"{latest.Tag.TrimStart('v')} is out", AboutHealth.Warning, $"Download it from {latest.PageUrl}")
                : new UpdateInfo("up to date", AboutHealth.Ok);
        }
        catch (Exception ex) when (ex is AppUpdateException or HttpRequestException or TaskCanceledException)
        {
            return new UpdateInfo("couldn't ask GitHub", AboutHealth.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Docker as the Docker container settings card sees it: "29.8.1 - engine 29.8.1 running", "29.8.1 - engine not
    /// running" or "not installed".
    /// </summary>
    public static async Task<string> DockerAsync(IPrerequisiteChecker checker, string container)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var docker = await checker.GetDockerStatusAsync(container, limit.Token);
            if (!docker.DesktopInstalled) return "not installed";
            var client = docker.ClientVersion ?? "installed";
            return docker.EngineRunning ? $"{client} - engine {docker.EngineVersion} running" : $"{client} - engine not running";
        }
        catch (OperationCanceledException)
        {
            return "didn't answer within 10 seconds";
        }
        catch (Exception ex)
        {
            return $"couldn't be asked: {ex.Message}";
        }
    }
}
