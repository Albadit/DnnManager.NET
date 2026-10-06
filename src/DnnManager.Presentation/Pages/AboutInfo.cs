using System.Diagnostics;
using DnnManager.Application.Abstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using DnnManager.Infrastructure.Diagnostics;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Settings → About: which DNN Manager this is, what it runs on and what it works with - each read from where it is (the
/// program, Windows, the registry), the update status from GitHub's releases (<see cref="AppUpdater"/>). Its data
/// folders are listed under it.
/// </summary>
internal static class AboutInfo
{
    public const string Repository = AppReleaseFeed.Repository;

    public static IEnumerable<InspectorSection> Sections(AppUpdater update, string? docker)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var commit = informational.Split('+', 2) is [_, var sha] && sha.Length > 0 ? sha[..Math.Min(12, sha.Length)] : null;
        var debug = assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITTrackingEnabled == true;
        var exe = Environment.ProcessPath;

        var app = new InspectorSection("DNN Manager", "the program");
        app.Add("Version", version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}");
        app.Add("Build", commit is null ? null : $"commit {commit}");
        if (exe is not null && File.Exists(exe)) app.Add("Built", File.GetLastWriteTime(exe).ToString("g"));
        app.Add("Program folder", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        app.Add("Release channel", debug ? "development (Debug build)" : informational.Split('+')[0].Contains('-') ? "pre-release" : "stable");
        // Why GitHub couldn't be reached is said under it; the release itself is installed with the title bar's Update button.
        app.Add("Update", update.StatusText, UpdateHealth(update.State), update.State == UpdateState.Unreachable ? update.Problem : null);
        app.Add("License", "MIT");
        app.Add("Repository", Repository);
        app.Add("Documentation", $"{Repository}/tree/main/.docs");
        yield return app;

        var runtime = new InspectorSection("Runs on", "this PC");
        runtime.Add(".NET", RuntimeInformation.FrameworkDescription);
        runtime.Add("Architecture", $"{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} process on {MachineInfo.Architecture} Windows");
        runtime.Add("Windows", MachineInfo.Windows);
        runtime.Add("Administrator", AdminElevation.IsAdministrator() ? "yes - it manages IIS" : "no",
            AdminElevation.IsAdministrator() ? Health.None : Health.Bad);
        yield return runtime;

        var components = new InspectorSection("Works with", "Windows and its own libraries");
        components.Add("IIS", MachineInfo.Iis ?? "not installed", MachineInfo.Iis is null ? Health.Bad : Health.None);
        components.Add(".NET Framework (sites)", MachineInfo.NetFramework ?? "not found", MachineInfo.NetFramework is null ? Health.Bad : Health.None);
        components.Add("Docker", docker ?? "checking…");
        components.Add("Microsoft.Web.Administration", LibraryVersion(typeof(Microsoft.Web.Administration.ServerManager)));
        components.Add("Microsoft.Data.SqlClient", LibraryVersion(typeof(Microsoft.Data.SqlClient.SqlConnection)));
        yield return components;
    }

    private static string LibraryVersion(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? type.Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// The dot: orange while a newer release waits (or is being installed), green when up to date, gray when GitHub
    /// couldn't be asked - nothing is known then - and red when the update failed.
    /// </summary>
    private static Health UpdateHealth(UpdateState state) => state switch
    {
        UpdateState.UpToDate => Health.Ok,
        UpdateState.Available or UpdateState.Downloading or UpdateState.Installing or UpdateState.Restarting => Health.Warning,
        UpdateState.Unreachable => Health.Unknown,
        UpdateState.Failed => Health.Bad,
        _ => Health.None
    };

    /// <summary>
    /// Docker as the Docker container settings card sees it (<see cref="IPrerequisiteChecker.GetDockerStatusAsync"/>):
    /// "29.8.1 - engine 29.8.1 running", "29.8.1 - engine not running" or "not installed".
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
    }
}
