using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

public enum UpdateState { Checking, UpToDate, Available, Unreachable, Downloading, Installing, Restarting, Failed }

/// <summary>
/// DNN Manager's own updates: whether GitHub has a newer release (once at start, when About is opened, and on Check for updates),
/// and the update itself - download and check the release's file, note the update (<see cref="UpdateRecord"/>), close
/// (the window saves where the user is, <see cref="WorkspaceService"/>), and leave the rest to the update helper (<see cref="UpdateHelper"/>), which installs it and starts the new
/// version. Until DNN Manager closes nothing is changed: a failure before that leaves this version running as it was.
/// </summary>
public sealed class AppUpdater : INotifyPropertyChanged
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(5), CleanupAfter = TimeSpan.FromMinutes(2);

    private readonly AppReleaseFeed _feed = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    private readonly UpdateDownloader _downloader = new(new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
    private readonly WorkspaceService _workspace;
    private readonly OperationRunner _runner;
    private readonly ActivityLog _log;
    // A single-file exe (installed or portable) has no DnnManager.dll beside it; a dotnet build's output has.
    private readonly Lazy<UpdateTarget?> _target = new(() =>
        UpdateTarget.Detect(Environment.ProcessPath, !File.Exists(Path.Combine(AppContext.BaseDirectory, "DnnManager.dll"))));
    private bool _started;
    private DateTime _checkedAt = DateTime.MinValue;
    private bool _checking, _updating;

    private readonly AppOptions _options;
    // Where a failed update's logs are kept (logs\update-failed-<version>.log) - the update's folder is cleaned up.
    private readonly string _logsDirectory;

    public AppUpdater(WorkspaceService workspace, OperationRunner runner, ActivityLog log, IOptions<AppOptions> options, AppDataPaths paths)
    {
        _workspace = workspace; _runner = runner; _log = log; _options = options.Value; _logsDirectory = paths.LogsDirectory;
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Current = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>
    /// Where the downloads and the helper go - one folder per version, removed a while after the next start. Only
    /// administrators may change it: the helper runs from it, and runs the package, with their rights.
    /// </summary>
    public static string WorkFolder => Path.Combine(PrivateTemp.Path, "update");

    public Version Current { get; }
    public UpdateState State { get; private set; } = UpdateState.Checking;
    /// <summary>GitHub's newest release, once known.</summary>
    public AppRelease? Latest { get; private set; }
    /// <summary>Why GitHub couldn't be asked, or why the update failed.</summary>
    public string? Problem { get; private set; }

    /// <summary>What an update replaces; null for a development build, which isn't updated but rebuilt.</summary>
    public UpdateTarget? Target => _target.Value;

    /// <summary>A newer release is there and this DNN Manager can install it - the Update button shows.</summary>
    public bool CanInstall => Latest is not null && Target is not null && AppReleaseFeed.IsNewer(Latest.Version, Current) &&
                              State is UpdateState.Available or UpdateState.Failed;

    public bool IsUpdating => State is UpdateState.Downloading or UpdateState.Installing or UpdateState.Restarting;

    /// <summary>The status in words - About's Update row.</summary>
    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"Up to date - v{Current} is the newest release",
        UpdateState.Available when Target is null => $"Update available - {Latest!.Tag} (a development build doesn't update itself)",
        UpdateState.Available => $"Update available - {Latest!.Tag}",
        UpdateState.Unreachable => "Unable to reach GitHub",
        UpdateState.Downloading => "Downloading update…",
        UpdateState.Installing => "Installing update…",
        UpdateState.Restarting => "Restarting…",
        _ => $"Update failed - {Problem}"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Checks once, a moment after the window is up - not again while DNN Manager runs (About and Check for updates
    /// ask then); cleans up the last update's files.
    /// </summary>
    public async void Start()
    {
        if (_started) return;
        _started = true;
        _ = CleanupLaterAsync();
        // Switched off in the settings: GitHub is asked only when the user asks (About).
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
        // A check while the button shows doesn't take it away.
        if (State is not (UpdateState.Available or UpdateState.Failed)) Set(UpdateState.Checking);
        try
        {
            var latest = await _feed.GetLatestAsync(CancellationToken.None);
            _checkedAt = DateTime.UtcNow;
            var failedForThis = State == UpdateState.Failed && Latest?.Version == latest.Version;
            Latest = latest;
            // A failed update of this same release keeps saying why it failed (and its button says Retry).
            if (failedForThis) Set(UpdateState.Failed, problem: Problem);
            else Set(AppReleaseFeed.IsNewer(latest.Version, Current) ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (AppUpdateException ex)
        {
            // What was known stays usable: a release found earlier can still be installed.
            if (State is UpdateState.Available or UpdateState.Failed) return;
            Set(UpdateState.Unreachable, problem: ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Downloads, checks and installs the newest release: saves where the user is, closes DNN Manager and lets the
    /// helper install it and start the new version. Anything that goes wrong before closing leaves this one running.
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

            Set(UpdateState.Downloading);
            // Off the UI thread: some thousand reads and a SHA-256 of the whole file.
            var package = await Task.Run(() => _downloader.DownloadAsync(release, asset, folder, null, CancellationToken.None));

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

            // The new version reads it to say whether the update worked; where the user is, the window saves as it closes.
            _workspace.Store.Save(new UpdateRecord
            {
                SavedUtc = DateTime.UtcNow, FromVersion = plan.FromVersion, ToVersion = plan.ToVersion, ResultFile = resultFile
            });

            Set(UpdateState.Restarting);
            _log.Info($"Updating DNN Manager {Current} to {release.Tag}: it closes, installs the update and opens again where you are.");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helper)! };
            start.ArgumentList.Add(UpdateHelper.Argument);
            start.ArgumentList.Add(planFile);
            if (AppRestart.Restart(start)) return;

            // "Stay" when asked about an unsaved password or a running operation: nothing changes, the update waits.
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

    /// <summary>
    /// A copy of this exe to run the update from - it must not run from the files it replaces. An installed one takes
    /// the native DLLs beside it along; a portable one has them inside.
    /// </summary>
    private static string CopyHelper(UpdateTarget target, string folder)
    {
        var exe = Environment.ProcessPath ?? throw new AppUpdateException("this program's file isn't known");
        // A folder of its own each time: a helper from an earlier attempt may still hold its files.
        var dir = Path.Combine(folder, $"helper-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(dir);
        var helper = Path.Combine(dir, "DnnManager-update.exe");
        File.Copy(exe, helper, overwrite: true);
        if (target.Kind == UpdateKind.Installer)
            foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*.dll"))
                File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), overwrite: true);
        return helper;
    }

    /// <summary>
    /// The last update's downloads and helper, once it has surely finished - unless an update is under way. Only
    /// <see cref="WorkFolder"/>: what DNN Manager 1.8.0 and older left in %TEMP% stays - deleting there, with
    /// administrator rights, would follow wherever another program of the user had pointed that folder. An update
    /// that failed keeps its logs first, in the logs folder - a helper of 1.8.1 or older doesn't keep them itself.
    /// </summary>
    private async Task CleanupLaterAsync()
    {
        await Task.Delay(CleanupAfter);
        if (_updating || IsUpdating) return;
        await Task.Run(() => CleanUp(WorkFolder, _logsDirectory));
    }

    /// <summary>
    /// Deletes every update's folder in <paramref name="workFolder"/> - keeping the logs of one that failed (its result
    /// says so, or the helper wrote none) as <c>update-failed-&lt;version&gt;.log</c> in <paramref name="logsDirectory"/>,
    /// unless its helper kept them there already.
    /// </summary>
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
