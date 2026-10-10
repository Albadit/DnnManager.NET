using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Updates;

/// <summary>What the update helper is to do - written by the running DNN Manager before it closes.</summary>
public sealed record UpdatePlan
{
    public required UpdateKind Kind { get; init; }
    /// <summary>The downloaded, checked Setup or portable exe.</summary>
    public required string Package { get; init; }
    /// <summary>The package's SHA-256 as GitHub lists it - checked again by the helper right before it uses the file.</summary>
    public required string PackageSha256 { get; init; }
    /// <summary>The package's size as GitHub lists it.</summary>
    public required long PackageSize { get; init; }
    /// <summary>The exe that is replaced (portable) or installed over (Setup) - and started again afterwards.</summary>
    public required string AppExe { get; init; }
    /// <summary>Setup: the installation is for all users (<c>/ALLUSERS</c>), not only this one (<c>/CURRENTUSER</c>).</summary>
    public bool AllUsers { get; init; }
    /// <summary>The DNN Manager that closes for the update - nothing is touched until it has.</summary>
    public required int WaitForProcessId { get; init; }
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public required string LogFile { get; init; }
    /// <summary>Where the helper writes the <see cref="UpdateResult"/> the started DNN Manager reads.</summary>
    public required string ResultFile { get; init; }
    /// <summary>
    /// When the update fails: where the helper keeps its log and Setup's (the user's logs folder) - the update's own
    /// folder is cleaned up after the next start. Null: not kept (a plan of 1.8.1 or older has none).
    /// </summary>
    public string? FailedLogFile { get; init; }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, UpdateJson.Options));

    public static UpdatePlan Read(string path) =>
        JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), UpdateJson.Options) ?? throw new InvalidDataException($"{path} is empty");
}

/// <summary>How the update went - read by the DNN Manager the helper starts.</summary>
public sealed record UpdateResult(bool Installed, string Message, string? LogFile)
{
    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, UpdateJson.Options));

    /// <summary>The result in <paramref name="path"/>; null when there is none (the helper didn't get that far) or it can't be read.</summary>
    public static UpdateResult? TryRead(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), UpdateJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// The update's second half, in a process of its own: a copy of the old DNN Manager's exe started with
/// <see cref="Argument"/> and a <see cref="UpdatePlan"/>, from a temporary folder - so nothing it runs from is replaced.
/// It waits for DNN Manager to close, installs the new version (Setup, silently with its progress window, or the
/// portable exe swapped with a backup), and starts DNN Manager again - the new version, or the old one when the update
/// failed. The old one is never left half replaced: Setup rolls back what it can't finish, the portable swap is one
/// rename, and a new portable exe that closes with an error right after starting is swapped back.
/// </summary>
public static class UpdateHelper
{
    public const string Argument = "--apply-update";

    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(15);

    public static bool IsHelper(string[] args) => args.Length >= 2 && args[0] == Argument;

    /// <summary>Runs the plan in <c>args[1]</c>. Never throws, never shows anything: the result is in the plan's result file and log.</summary>
    public static int Run(string[] args)
    {
        UpdatePlan plan;
        try { plan = UpdatePlan.Read(args[1]); }
        catch (Exception) { return 2; }
        // Setup and the new version: without the variables that would make them load other code.
        return Run(plan, start =>
        {
            ChildEnvironment.ForSelf(start.Environment);
            return Process.Start(start);
        }, Watch, TimeSpan.FromMinutes(2));
    }

    /// <param name="start">Starts a process (Setup, DNN Manager) - replaced in tests.</param>
    /// <param name="watch">How long a new portable exe must keep running before its backup is deleted.</param>
    /// <param name="closing">How long DNN Manager may take to close.</param>
    public static int Run(UpdatePlan plan, Func<ProcessStartInfo, Process?> start, TimeSpan watch, TimeSpan closing)
    {
        var log = new HelperLog(plan.LogFile);
        log.Write($"Updating DNN Manager {plan.FromVersion} to {plan.ToVersion} ({plan.Kind}): {plan.AppExe}");
        try
        {
            if (!WaitForExit(plan.WaitForProcessId, closing))
            {
                // It still runs: nothing was changed, and nothing is started - it is open.
                Finish(plan, log, false, $"DNN Manager didn't close within {closing.TotalMinutes:0.#} minutes - nothing was changed.");
                return 1;
            }
            log.Write("DNN Manager has closed.");

            // A backup left by an earlier update isn't this one's: deleted before anything changes, so only the backup
            // this update makes is ever put back in place of the exe.
            if (plan.Kind == UpdateKind.Portable && File.Exists(BackupOf(plan)))
            {
                try { File.Delete(BackupOf(plan)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Finish(plan, log, false, $"{BackupOf(plan)}, left by an earlier update, can't be deleted ({ex.Message}) - nothing was changed.");
                    Launch(plan, start, log);
                    return 1;
                }
            }

            // The package is checked again now, and held open so it can't be changed while it is copied or run: it is
            // used with administrator rights.
            using var package = OpenChecked(plan, out var problem);
            if (package is null)
            {
                Finish(plan, log, false, $"The downloaded update can't be used: {problem} - nothing was changed.");
                Launch(plan, start, log);
                return 1;
            }
            var failure = plan.Kind == UpdateKind.Portable ? ReplacePortable(plan, package, log) : RunSetup(plan, start, log);
            if (failure is not null)
            {
                Finish(plan, log, false, failure);
                Launch(plan, start, log);
                return 1;
            }

            Finish(plan, log, true, $"DNN Manager {plan.ToVersion} was installed.");
            var started = Launch(plan, start, log);
            if (plan.Kind == UpdateKind.Portable)
            {
                if (started is not null && started.WaitForExit(watch) && started.ExitCode != 0)
                {
                    log.Write($"The new version closed right after starting (exit code {started.ExitCode}) - putting {plan.FromVersion} back.");
                    RestorePortable(plan, log);
                    Finish(plan, log, false, $"DNN Manager {plan.ToVersion} closed right after it started (exit code {started.ExitCode}) - {plan.FromVersion} was put back.");
                    Launch(plan, start, log);
                    return 1;
                }
                TryDelete(BackupOf(plan), log);
            }
            log.Write("Done.");
            return 0;
        }
        catch (Exception ex)
        {
            log.Write($"Failed: {ex}");
            if (plan.Kind == UpdateKind.Portable) RestorePortable(plan, log);
            Finish(plan, log, false, $"The update failed: {ex.Message}");
            Launch(plan, start, log);
            return 1;
        }
    }

    /// <summary>
    /// The package opened so that nobody can write to it, as long as it is open - once its size and SHA-256 are the
    /// release's; null when they aren't (<paramref name="problem"/> says why).
    /// </summary>
    internal static FileStream? OpenChecked(UpdatePlan plan, out string? problem)
    {
        problem = null;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(plan.Package, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != plan.PackageSize) problem = $"it is {stream.Length:N0} bytes, GitHub lists {plan.PackageSize:N0}";
            else if (plan.PackageSha256.Length == 0) problem = "GitHub lists no SHA-256 for it";
            else if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(plan.PackageSha256, StringComparison.OrdinalIgnoreCase))
                problem = "its SHA-256 isn't the one GitHub lists";
            if (problem is null)
            {
                stream.Position = 0;
                return stream;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }
        stream?.Dispose();
        return null;
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    private static string StagedOf(UpdatePlan plan) => plan.AppExe + ".new";
    private static string BackupOf(UpdatePlan plan) => plan.AppExe + ".old";

    /// <summary>
    /// Puts the new exe in the old one's place, under the old one's name (shortcuts and the sign-in task keep working),
    /// keeping the old one as a backup. Null when it worked, else why not - the old exe is then in place.
    /// </summary>
    private static string? ReplacePortable(UpdatePlan plan, Stream package, HelperLog log)
    {
        var staged = StagedOf(plan);
        var backup = BackupOf(plan);
        // Next to the exe, so the swap is a rename on one volume - copied from the checked file, still held open.
        using (var target = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            package.CopyTo(target);
        Exception? last = null;
        // The closed process's file, or an antivirus scan, can hold it for a moment.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Replace(staged, plan.AppExe, backup, ignoreMetadataErrors: true);
                log.Write($"Replaced {plan.AppExe} (the old one is {backup} until the new one runs).");
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(500);
            }
        }
        log.Write($"Couldn't replace {plan.AppExe}: {last}");
        RestorePortable(plan, log);
        TryDelete(staged, log);
        return $"{plan.AppExe} couldn't be replaced: {last?.Message}";
    }

    /// <summary>
    /// The old exe back in its place, when the swap didn't finish or the new one didn't run - the backup this update made:
    /// one an earlier update left behind was deleted before anything was changed.
    /// </summary>
    private static void RestorePortable(UpdatePlan plan, HelperLog log)
    {
        var backup = BackupOf(plan);
        if (!File.Exists(backup)) return;
        try
        {
            File.Move(backup, plan.AppExe, overwrite: true);
            log.Write($"Put {plan.FromVersion} back: {plan.AppExe}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Couldn't put {backup} back: {ex.Message}");
        }
    }

    /// <summary>How often the helper notes in its log that Setup is still running.</summary>
    private static readonly TimeSpan SetupNotice = TimeSpan.FromMinutes(30);

    /// <summary>Setup's log, beside the helper's.</summary>
    private static string SetupLogOf(UpdatePlan plan) => Path.ChangeExtension(plan.LogFile, ".setup.log");

    /// <summary>
    /// Runs Setup over the installation - its progress window shows, it asks nothing - and waits for it as long as it
    /// runs: DNN Manager is started only once Setup has ended, never while it may still be replacing its files (a slow
    /// disk, an antivirus scan). Ending a Setup half way would leave less than either version. Null when it worked.
    /// </summary>
    private static string? RunSetup(UpdatePlan plan, Func<ProcessStartInfo, Process?> start, HelperLog log)
    {
        var setupLog = SetupLogOf(plan);
        var info = new ProcessStartInfo(plan.Package) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Package)! };
        foreach (var argument in SetupArguments(plan.AllUsers, setupLog)) info.ArgumentList.Add(argument);
        log.Write($"Running {plan.Package} {string.Join(' ', info.ArgumentList)}");
        using var setup = start(info);
        if (setup is null) return "Setup couldn't be started.";
        var waited = TimeSpan.Zero;
        while (!setup.WaitForExit(SetupNotice))
        {
            waited += SetupNotice;
            log.Write($"Setup is still running after {waited.TotalMinutes:0} minutes - waiting for it; DNN Manager starts once it has ended (see {setupLog}).");
        }
        log.Write($"Setup ended with exit code {setup.ExitCode}.");
        return setup.ExitCode switch
        {
            0 => null,
            2 or 5 => "Setup was cancelled.",
            7 => $"Setup couldn't prepare the installation - see {setupLog}.",
            _ => $"Setup failed (exit code {setup.ExitCode}) - see {setupLog}."
        };
    }

    /// <summary>
    /// Inno Setup's switches: no questions, its progress window, no reboot, into the installation it updates - always
    /// with <c>/ALLUSERS</c> or <c>/CURRENTUSER</c>: Setup installs for all users unless told otherwise (from 1.8.2), so
    /// an installation for the current user only is updated where it is, not joined by a second one in Program Files.
    /// </summary>
    public static IReadOnlyList<string> SetupArguments(bool allUsers, string setupLog) =>
        ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/SP-", allUsers ? "/ALLUSERS" : "/CURRENTUSER", $"/LOG={setupLog}"];

    private static Process? Launch(UpdatePlan plan, Func<ProcessStartInfo, Process?> start, HelperLog log)
    {
        try
        {
            var process = start(new ProcessStartInfo(plan.AppExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.AppExe)! });
            log.Write($"Started {plan.AppExe}.");
            return process;
        }
        catch (Exception ex)
        {
            log.Write($"Couldn't start {plan.AppExe}: {ex.Message}");
            return null;
        }
    }

    private static void Finish(UpdatePlan plan, HelperLog log, bool installed, string message)
    {
        log.Write(message);
        // A failed update's evidence outlives the update's folder: "Show log" opens the kept copy.
        var logFile = plan.LogFile;
        if (!installed && plan.FailedLogFile is { } kept)
        {
            if (KeepFailedLog(plan.LogFile, SetupLogOf(plan), kept, out var problem)) logFile = kept;
            else log.Write($"Couldn't keep the log as {kept}: {problem}");
        }
        try { new UpdateResult(installed, message, logFile).Write(plan.ResultFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"Couldn't write {plan.ResultFile}: {ex.Message}"); }
    }

    /// <summary>How many failed updates' logs are kept in the logs folder - the newest.</summary>
    internal const int FailedLogsKept = 2;

    /// <summary>
    /// Copies a failed update's log, and Setup's when there is one, into <paramref name="destination"/>
    /// (<c>logs\update-failed-&lt;version&gt;.log</c>), and deletes all but the newest <see cref="FailedLogsKept"/> such
    /// files beside it. The logs folder is the user's, and this runs with administrator rights: a file already there is
    /// deleted (a link removed is only the link) and the copy made new - never written through whatever was there.
    /// </summary>
    public static bool KeepFailedLog(string updateLog, string? setupLog, string destination, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(updateLog) && (setupLog is null || !File.Exists(setupLog)))
            {
                problem = "there is no log to keep";
                return false;
            }
            var folder = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(folder);
            if (File.Exists(destination)) File.Delete(destination);
            using (var writer = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)))
            {
                writer.WriteLine("# DNN Manager update that failed");
                foreach (var (title, file) in new[] { ("The update helper's log", updateLog), ("Setup's log", setupLog) })
                {
                    if (file is null || !File.Exists(file)) continue;
                    writer.WriteLine();
                    writer.WriteLine($"## {title} ({file})");
                    writer.WriteLine();
                    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    writer.Write(reader.ReadToEnd());
                }
            }
            foreach (var old in new DirectoryInfo(folder).GetFiles("update-failed-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(FailedLogsKept))
                try { old.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next time */ }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>The file a failed update to <paramref name="toVersion"/> keeps its logs in, in <paramref name="logsDirectory"/>.</summary>
    public static string FailedLogPath(string logsDirectory, string toVersion) =>
        Path.Combine(logsDirectory, $"update-failed-{toVersion}.log");

    private static void TryDelete(string path, HelperLog log)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"Couldn't delete {path}: {ex.Message}"); }
    }

    private sealed class HelperLog(string path)
    {
        public void Write(string line)
        {
            try { File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nowhere to say it */ }
        }
    }
}
