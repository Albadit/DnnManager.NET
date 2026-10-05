using System.IO.Compression;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.IntegrationTests.Support;

/// <summary>DNN releases from a downloaded install package: one release, extracted from the zip like DNN Manager does.</summary>
public sealed class TestDnnPackages(string zip, string version) : IDnnReleaseService, IDnnPackageInstaller
{
    public DnnRelease Release { get; } = new(version, $"v{version}", new Uri(zip).AbsoluteUri);

    public Task<Result<DnnRelease>> GetReleaseAsync(string apiUrl, string? version, CancellationToken ct) =>
        Task.FromResult(Result<DnnRelease>.Ok(Release));

    public Task<Result<DnnReleaseList>> ListReleasesAsync(string apiUrl, CancellationToken ct) =>
        Task.FromResult(Result<DnnReleaseList>.Ok(new DnnReleaseList([Release])));

    public IReadOnlyList<string> KnownReleaseApis => ["https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases"];

    public Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct)
    {
        ZipFile.ExtractToDirectory(zip, projectDirectory, overwriteFiles: true);
        reporter.Success($"Extracted DNN {release.Version} into {projectDirectory}");
        return Task.FromResult(Result.Ok());
    }

    // The integration tests install - they don't upgrade.
    public Task<Result> ExtractUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct) =>
        Task.FromResult(Result.Fail("The test packages have no upgrade package."));

    public Task<Result> ExtractLocalUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct) =>
        Task.FromResult(Result.Fail("The test packages don't upgrade."));

    public bool IsKept(DnnRelease release) => true;
}

/// <summary>IIS's Windows features - nothing to enable: the tests use IIS Express.</summary>
public sealed class NoIisFeatures : IPrerequisiteChecker
{
    public Task<Result> EnsureIisFeaturesAsync(IProgressReporter reporter, IUserPrompt prompt, CancellationToken ct) => Task.FromResult(Result.Ok());

    public Task<IReadOnlyDictionary<string, bool>> GetIisFeatureStatesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());

    public Task<DockerStatus> GetDockerStatusAsync(string containerName, CancellationToken ct) =>
        Task.FromResult(new DockerStatus(false, null, false, null, null, null));

    public Task<Result> InstallDockerDesktopAsync(IProgressReporter reporter, CancellationToken ct) => Task.FromResult(Result.Ok());

    public Result StartDockerDesktop() => Result.Ok();
}

/// <summary>IIS that is there but must be left alone: it answers questions and records everything that would change it.</summary>
public sealed class UntouchedIis : IIisManager
{
    public List<string> Changes { get; } = [];

    private Result Change(string what)
    {
        lock (Changes) Changes.Add(what);
        return Result.Ok();
    }

    public Result CreateSite(string siteName, string physicalPath, string hostname, int port) => Change($"CreateSite {siteName}");
    public Result RemoveSite(string siteName) => Change($"RemoveSite {siteName}");
    public Result StartSite(string siteName) => Change($"StartSite {siteName}");
    public Result StopSite(string siteName) => Change($"StopSite {siteName}");
    public Result RestartSite(string siteName) => Change($"RestartSite {siteName}");
    public Result RecycleAppPool(string siteName) => Change($"RecycleAppPool {siteName}");
    public Result EnableUserProfile(string siteName) => Change($"EnableUserProfile {siteName}");
    public Result GrantPermissions(string path, IEnumerable<string> identities) => Change($"GrantPermissions {path}");
    public Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct) => Task.FromResult(Change($"RemoveAppPoolProfile {poolName}"));
    public Task<Result> ControlServerAsync(IisServerAction action, CancellationToken ct) => Task.FromResult(Change($"ControlServer {action}"));

    public string? GetLogDirectory(string siteName) => null;
    public string AppPoolIdentity(string siteName) => $@"IIS APPPOOL\{siteName}";
    public bool IsAvailable() => true;
    public IisServerState GetServerState() => IisServerState.Running;
    public IReadOnlyDictionary<string, string> GetSiteStates() => new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IisSiteRuntime>? GetSiteRuntimes() => new Dictionary<string, IisSiteRuntime>();
    public IReadOnlyDictionary<string, SiteTraffic> GetSiteTraffic() => new Dictionary<string, SiteTraffic>();
    public IReadOnlyDictionary<string, long> GetRequestsServed() => new Dictionary<string, long>();
}

/// <summary>Answers no to every question (never drops a database) and remembers them.</summary>
public sealed class TestPrompt : IUserPrompt
{
    public List<string> Questions { get; } = [];

    public Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default)
    {
        lock (Questions) Questions.Add(question);
        return Task.FromResult(false);
    }
}

/// <summary>Everything an operation reports, as the Output panel would show it - to check messages and that no secret is in them.</summary>
public sealed class RecordingReporter : IProgressReporter
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_lines) return _lines.ToList(); }
    }

    public string Text => string.Join(Environment.NewLine, Lines);

    public void Step(string title) => Add($"STEP {title}");
    public void Info(string message) => Add($"INFO {message}");
    public void Success(string message) => Add($"OK   {message}");
    public void Fail(string message) => Add($"FAIL {message}");
    public void Warn(string message) => Add($"WARN {message}");
    public void Progress(string message) => Add($"...  {message}");
    public void Step(string title, string name) => Add($"STEP {title} [{name}]");
    public void Fail(string message, IReadOnlyList<string> details, string? hint) =>
        Add($"FAIL {message}{string.Concat(details.Select(d => $" | {d}"))}{(hint is null ? "" : $" -> {hint}")}");
    public void Plan(params string[] stages) => Add($"PLAN {string.Join(", ", stages)}");
    public void Context(string text) => Add($"CTX  {text}");
    public void Fact(string name, string value) => Add($"FACT {name} = {value}");
    public void Link(string url) => Add($"LINK {url}");

    private void Add(string line)
    {
        lock (_lines) _lines.Add(line);
    }
}