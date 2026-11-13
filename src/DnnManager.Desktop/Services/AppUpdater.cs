using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Services;

public enum UpdateState { Checking, UpToDate, Available, Unreachable, Downloading, Installing, Restarting, Failed }

/// <summary>
/// DNN Manager's own updates, as the Windows app's (src/DnnManager.Presentation/Services/AppUpdater.cs): whether GitHub has
/// a newer release (a moment after the start, on Check for updates, and on About), and the update itself - download the
/// release's Setup or portable exe, check its size, SHA-256 and version (<see cref="UpdateDownloader"/>), note the update
/// (<see cref="UpdateRecord"/>), close (the window saves where the user is) and leave the rest to the update helper
/// (<see cref="UpdateHelper"/>, a copy of this exe), which installs it and starts the new version. Until DNN Manager
/// closes nothing is changed. Windows only: on macOS and Linux the program is one file the user downloads
/// (<see cref="Target"/> is null) - the check still says when a newer one is out.
/// </summary>
public sealed class AppUpdater : INotifyPropertyChanged
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(5), CleanupAfter = TimeSpan.FromMinutes(2);

    private readonly AppReleaseFeed _feed = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    private readonly UpdateDownloader _downloader = new(new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
    private readonly WorkspaceService _workspace;
    private readonly OperationRunner _runner;
    private readonly ActivityLog _log;
    private readonly AppOptions _options;
    private readonly string _logsDirectory;
    // A single-file exe (installed or portable) has no DnnManager.Desktop.dll beside it; a dotnet build's output has.
    private readonly Lazy<UpdateTarget?> _target = new(() => OperatingSystem.IsWindows()
        ? UpdateTarget.Detect(Environment.ProcessPath, !File.Exists(Path.Combine(AppContext.BaseDirectory, "DnnManager.Desktop.dll")))
        : null);
    private bool _started, _checking, _updating;
    private DateTime _checkedAt = DateTime.MinValue;

    public AppUpdater(WorkspaceService workspace, OperationRunner runner, ActivityLog log, IOptions<AppOptions> options, AppDataPaths paths)
    {
        _workspace = workspace; _runner = runner; _log = log; _options = options.Value; _logsDirectory = paths.LogsDirectory;
        var version = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
        Current = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        Informational = typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                        ?? Current.ToString();
    }

    /// <summary>Where the downloads and the helper go - one folder per version, removed a while after the next start.</summary>
    public static string WorkFolder => Path.Combine(PrivateTemp.Path, "update");

    public Version Current { get; }

    /// <summary>The version as released - with its pre-release label (2.0.0-rc1).</summary>
    public string Informational { get; }

    public UpdateState State { get; private set; } = UpdateState.Checking;
    public AppRelease? Latest { get; private set; }
    public string? Problem { get; private set; }
    public double DownloadProgress { get; private set; }

    /// <summary>What an update replaces; null for a development build and on macOS and Linux.</summary>
    public UpdateTarget? Target => _target.Value;

    /// <summary>A newer release is there and this DNN Manager can install it - the Update button shows.</summary>
    public bool CanInstall => Latest is not null && Target is not null && AppReleaseFeed.IsNewer(Latest.Version, Current) &&
                              State is UpdateState.Available or UpdateState.Failed;

    public bool IsUpdating => State is UpdateState.Downloading or UpdateState.Installing or UpdateState.Restarting;

    /// <summary>The state in words - Check for updates, About.</summary>
    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"Up to date - v{Current} is the newest release",
        UpdateState.Available when Target is null && !OperatingSystem.IsWindows() =>
            $"Update available - {Latest!.Tag}: download it from {Latest.PageUrl}",
        UpdateState.Available when Target is null => $"Update available - {Latest!.Tag} (a development build doesn't update itself)",
        UpdateState.Available => $"Update available - {Latest!.Tag}",
        UpdateState.Unreachable => "Unable to reach GitHub",
        UpdateState.Downloading => "Downloading update…",
        UpdateState.Installing => "Installing update…",
        UpdateState.Restarting => "Restarting…",
        _ => $"Update failed - {Problem}"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Checks once, a moment after the window is up (unless switched off); cleans up the last update's files.</summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        _ = CleanupLaterAsync();
        if (!_options.CheckForUpdatesAtStart) return;
        await Task.Delay(FirstCheck);
        await CheckAsync();
    }

    /// <summary>Asks GitHub again - unless it was asked in the last <paramref name="unlessWithin"/>.</summary>
    public async Task CheckAsync(TimeSpan? unlessWithin = null)
    {
        if (_checking || IsUpdating) return;
        if (unlessWithin is { } recent && DateTime.UtcNow - _checkedAt < recent && State != UpdateState.Unreachable) return;
        _checking = true;
        if (State is not (UpdateState.Available or UpdateState.Failed)) Set(UpdateState.Checking);
        try
        {
            var latest = await _feed.GetLatestAsync(CancellationToken.None);
            _checkedAt = DateTime.UtcNow;
            var failedForThis = State == UpdateState.Failed && Latest?.Version == latest.Version;
            Latest = latest;
            if (failedForThis) Set(UpdateState.Failed, problem: Problem);
            else Set(AppReleaseFeed.IsNewer(latest.Version, Current) ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (AppUpdateException ex)
        {
            if (State is UpdateState.Available or UpdateState.Failed) return;
            Set(UpdateState.Unreachable, problem: ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Downloads, checks and installs the newest release: notes the update, closes DNN Manager and lets the helper install it
    /// and start the new version. Anything that goes wrong before closing leaves this one running.
    /// </summary>
    public async Task UpdateAsync()
    {
        if (_updating || !CanInstall || Latest is not { } release || Target is not { } target) return;
        if (_runner.IsBusy)
        {
            Toast.Show($"'{_runner.Current}' is still running - update once it has finished.", ToastKind.Warning);
            return;
        }
        _updating = true;
        try
        {
            var asset = release.AssetFor(target.Kind)
                        ?? throw new AppUpdateException($"release {release.Tag} has no {(target.Kind == UpdateKind.Installer ? "Setup" : "portable exe")} for this PC");
            var folder = Path.Combine(WorkFolder, release.Version.ToString());

            DownloadProgress = 0;
            Set(UpdateState.Downloading);
            var progress = new Progress<double>(p =>
            {
                if (Math.Floor(p * 100) <= Math.Floor(DownloadProgress * 100)) return;
                DownloadProgress = p;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadProgress)));
            });
            var package = await Task.Run(() => _downloader.DownloadAsync(release, asset, folder, progress, CancellationToken.None));

            Set(UpdateState.Installing);
            var helper = await Task.Run(() => CopyHelper(target, folder));
            var resultFile = Path.Combine(folder, "result.json");
            File.Delete(resultFile);
            var plan = new UpdatePlan
            {
                Kind = target.Kind, Package = package, PackageSha256 = asset.Sha256 ?? "", PackageSize = asset.Size,
                AppExe = target.AppExe, AllUsers = target.AllUsers,
                WaitForProcessId = Environment.ProcessId, FromVersion = Current.ToString(), ToVersion = release.Version.ToString(),
                LogFile = Path.Combine(folder, "update.log"), ResultFile = resultFile,
                FailedLogFile = UpdateHelper.FailedLogPath(_logsDirectory, release.Version.ToString())
            };
            var planFile = Path.Combine(folder, "plan.json");
            plan.Write(planFile);
            _workspace.Store.Save(new UpdateRecord
            {
                SavedUtc = DateTime.UtcNow, FromVersion = plan.FromVersion, ToVersion = plan.ToVersion, ResultFile = resultFile
            });

            Set(UpdateState.Restarting);
            _log.Info($"Updating DNN Manager {Current} to {release.Tag}: it closes, installs the update and opens again where you are.");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helper)! };
            start.ArgumentList.Add(UpdateHelper.Argument);
            start.ArgumentList.Add(planFile);
            if (await AppRestart.RestartAsync(start)) return;

            // "Keep running" when asked about a running operation: nothing changes, the update waits.
            _workspace.Store.Delete<UpdateRecord>();
            Set(UpdateState.Available);
            Toast.Show("DNN Manager stayed open - the update is ready when you are.");
        }
        catch (Exception ex) when (ex is AppUpdateException or IOException or UnauthorizedAccessException)
        {
            _workspace.Store.Delete<UpdateRecord>();
            Set(UpdateState.Failed, problem: ex.Message);
            _log.Warn($"The update to {release.Tag} failed: {ex.Message} - DNN Manager {Current} keeps running.");
            Toast.Show($"The update to {release.Tag} failed: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>A copy of this exe to run the update from - it mustn't run from the file it replaces.</summary>
    private static string CopyHelper(UpdateTarget target, string folder)
    {
        var exe = Environment.ProcessPath ?? throw new AppUpdateException("this program's file isn't known");
        var dir = Path.Combine(folder, $"helper-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(dir);
        var helper = Path.Combine(dir, "DnnManager-update.exe");
        File.Copy(exe, helper, overwrite: true);
        // An installed one may have native libraries beside it (a self-extracting single file has them inside).
        if (target.Kind == UpdateKind.Installer)
            foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.dll"))
                File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), overwrite: true);
        return helper;
    }

    private async Task CleanupLaterAsync()
    {
        await Task.Delay(CleanupAfter);
        if (_updating || IsUpdating || !OperatingSystem.IsWindows()) return;
        await Task.Run(() => CleanUp(WorkFolder, _logsDirectory));
    }

    /// <summary>Deletes every update's folder - keeping a failed one's logs in the logs folder first.</summary>
    internal static void CleanUp(string workFolder, string logsDirectory)
    {
        if (!Directory.Exists(workFolder)) return;
        foreach (var dir in Directory.EnumerateDirectories(workFolder))
        {
            var updateLog = Path.Combine(dir, "update.log");
            var result = UpdateResult.TryRead(Path.Combine(dir, "result.json"));
            var kept = UpdateHelper.FailedLogPath(logsDirectory, Path.GetFileName(dir));
            if (result is not { Installed: true } && File.Exists(updateLog) && !string.Equals(result?.LogFile, kept, StringComparison.OrdinalIgnoreCase))
                UpdateHelper.KeepFailedLog(updateLog, Path.ChangeExtension(updateLog, ".setup.log"), kept, out _);
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use - next time */ }
        }
    }

    private void Set(UpdateState state, string? problem = null)
    {
        State = state;
        Problem = problem;
        if (Dispatcher.UIThread.CheckAccess()) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        else Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)));
    }
}
