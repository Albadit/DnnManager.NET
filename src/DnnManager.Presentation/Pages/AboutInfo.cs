using System.Diagnostics;
using DnnManager.Application.Abstractions;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using DnnManager.Infrastructure.Diagnostics;
using DnnManager.Presentation.Pages.Projects;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Settings → About: which DNN Manager this is, what it runs on and what it works with - each read from where it is (the
/// program, Windows, the registry), the update status from GitHub's releases. Its data folders are listed under it.
/// </summary>
internal static class AboutInfo
{
    public const string Repository = "https://github.com/Bond-for-web-solutions/DnnManager.NET";
    private const string LatestRelease = "https://api.github.com/repos/Bond-for-web-solutions/DnnManager.NET/releases/latest";

    /// <summary>What GitHub says the newest release is - or why it couldn't be asked.</summary>
    public sealed record UpdateStatus(string Text, Health Health, string? Link = null);

    public static IEnumerable<InspectorSection> Sections(UpdateStatus? update, string? docker)
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
        app.Add("Update", update?.Text ?? "checking GitHub's releases…", update?.Health ?? Health.None, update?.Link);
        app.Add("License", "MIT");
        app.Add("Repository", Repository);
        app.Add("Documentation", $"{Repository}/tree/main/docs");
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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Compares this version with GitHub's newest release.</summary>
    public static async Task<UpdateStatus> CheckUpdateAsync()
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.UserAgent.ParseAdd("DnnManager");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new UpdateStatus($"couldn't check - GitHub answered {(int)response.StatusCode}", Health.Warning);
            var release = await response.Content.ReadFromJsonAsync<Release>();
            if (release?.Tag is not { } tag || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return new UpdateStatus("couldn't check - GitHub's answer has no version", Health.Warning);
            return current is null || latest > new Version(current.Major, current.Minor, Math.Max(current.Build, 0))
                ? new UpdateStatus($"{tag} is available", Health.Warning, release.Url)
                : new UpdateStatus($"up to date - {tag} is the newest release", Health.Ok);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return new UpdateStatus($"couldn't check - {ex.Message}", Health.Warning);
        }
    }

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

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, [property: JsonPropertyName("html_url")] string? Url);
}
