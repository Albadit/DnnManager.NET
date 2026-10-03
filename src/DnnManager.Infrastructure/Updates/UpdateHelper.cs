using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnnManager.Infrastructure.Updates;

/// <summary>What the update helper is to do - written by the running DNN Manager before it closes.</summary>
public sealed record UpdatePlan
{
    public required UpdateKind Kind { get; init; }
    /// <summary>The downloaded, checked Setup or portable exe.</summary>
    public required string Package { get; init; }
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
        return Run(plan, start => Process.Start(start), Watch, TimeSpan.FromMinutes(2));
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

            var failure = plan.Kind == UpdateKind.Portable ? ReplacePortable(plan, log) : RunSetup(plan, start, log);
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
    private static string? ReplacePortable(UpdatePlan plan, HelperLog log)
    {
        var staged = StagedOf(plan);
        var backup = BackupOf(plan);
        // Next to the exe, so the swap is a rename on one volume.
        File.Copy(plan.Package, staged, overwrite: true);
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

    /// <summary>The old exe back in its place, when the swap didn't finish or the new one didn't run.</summary>
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

    /// <summary>Runs Setup over the installation - its progress window shows, it asks nothing. Null when it worked.</summary>
    private static string? RunSetup(UpdatePlan plan, Func<ProcessStartInfo, Process?> start, HelperLog log)
    {
        var setupLog = Path.ChangeExtension(plan.LogFile, ".setup.log");
        var info = new ProcessStartInfo(plan.Package) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Package)! };
        foreach (var argument in SetupArguments(plan.AllUsers, setupLog)) info.ArgumentList.Add(argument);
        log.Write($"Running {plan.Package} {string.Join(' ', info.ArgumentList)}");
        using var setup = start(info);
        if (setup is null) return "Setup couldn't be started.";
        if (!setup.WaitForExit(TimeSpan.FromMinutes(30))) return $"Setup was still running after 30 minutes - see {setupLog}.";
        log.Write($"Setup ended with exit code {setup.ExitCode}.");
        return setup.ExitCode switch
        {
            0 => null,
            2 or 5 => "Setup was cancelled.",
            7 => $"Setup couldn't prepare the installation - see {setupLog}.",
            _ => $"Setup failed (exit code {setup.ExitCode}) - see {setupLog}."
        };
    }

    /// <summary>Inno Setup's switches: no questions, its progress window, no reboot, into the installation it updates.</summary>
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
        try { new UpdateResult(installed, message, plan.LogFile).Write(plan.ResultFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"Couldn't write {plan.ResultFile}: {ex.Message}"); }
    }

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
