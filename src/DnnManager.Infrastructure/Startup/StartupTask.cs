using System.Security;
using System.Security.Principal;
using System.Text;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Startup;

/// <summary>
/// "Start DNN Manager when you sign in": a scheduled task that starts it at the user's logon with the highest
/// privileges. A plain startup entry (the Run registry key, or the Startup folder) would ask for Administrator rights
/// with a UAC prompt at every sign-in, since DNN Manager runs elevated; a task registered while elevated doesn't.
/// Only for a DNN Manager that nobody but administrators can change (installed for all users, in Program Files): the
/// task would start whatever another program put in its place, with administrator rights and without asking. And only
/// at sign-in - not on demand, so no other program can start it with those rights either.
/// The task starts DNN Manager through its launcher (<see cref="LaunchEnvironment.LauncherFileName"/>, beside an
/// installed DnnManager.exe): a task with an interactive token runs in the user's environment, whose .NET variables
/// would otherwise load a profiler or open a diagnostic port in DNN Manager, elevated, before its own code runs.
/// </summary>
public sealed class StartupTask(ProcessRunner process)
{
    public const string TaskName = "DNN Manager";

    private readonly ProcessRunner _process = process;

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>
    /// The DNN Manager exe the task starts, or null when there is no task ("" when it can't be read). A task that
    /// starts the launcher counts as starting the DnnManager.exe beside it - the launcher starts nothing else.
    /// </summary>
    public async Task<string?> GetTargetAsync(CancellationToken ct = default) =>
        await GetCommandAsync(ct) is { } command ? (command.Length > 0 ? AppOf(command) : command) : null;

    /// <summary>The exe in the task's action as it is written, or null when there is no task.</summary>
    private async Task<string?> GetCommandAsync(CancellationToken ct)
    {
        var query = await _process.RunAsync(Schtasks, ["/Query", "/TN", TaskName, "/XML"], ct);
        if (!query.Success) return null;
        // <Command>C:\…\DnnManager-launcher.exe</Command> (C:\…\DnnManager.exe up to 1.8.1)
        const string open = "<Command>", close = "</Command>";
        var start = query.StdOut.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : query.StdOut.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? "" : System.Net.WebUtility.HtmlDecode(query.StdOut[(start + open.Length)..end]).Trim().Trim('"');
    }

    /// <summary>The DnnManager.exe a task's command starts: the one beside the launcher, or the command itself.</summary>
    internal static string AppOf(string command) =>
        string.Equals(Path.GetFileName(command), LaunchEnvironment.LauncherFileName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(command) ?? "", LaunchEnvironment.AppFileName)
            : command;

    /// <summary>
    /// What the task runs for <paramref name="exePath"/>: the launcher beside it when there is one (an installed DNN
    /// Manager), else the exe itself (a portable one, which the task refuses anyway when others can change its folder).
    /// </summary>
    internal static string CommandFor(string exePath) => LaunchEnvironment.LauncherBeside(exePath) ?? exePath;

    /// <summary>Why <paramref name="exePath"/> can't be started by the task, or null when it can.</summary>
    public static string? Refusal(string exePath) =>
        TrustedPrograms.IsElevated ? RefusalFor(exePath, TrustOf(exePath, CommandFor(exePath))) : null;

    /// <summary>
    /// Whether only administrators can change what the task runs - each of <paramref name="files"/> (DnnManager.exe and
    /// the launcher that starts it): others can change one of them, or that couldn't be read for one (in use, say).
    /// </summary>
    private static TrustedPrograms.Trust TrustOf(params string[] files)
    {
        var trust = files.Distinct(StringComparer.OrdinalIgnoreCase).Select(TrustedPrograms.AdminOnly).ToList();
        return trust.Contains(TrustedPrograms.Trust.OthersCanChange) ? TrustedPrograms.Trust.OthersCanChange
            : trust.Contains(TrustedPrograms.Trust.Unknown) ? TrustedPrograms.Trust.Unknown
            : TrustedPrograms.Trust.AdminOnly;
    }

    private static string? RefusalFor(string exePath, TrustedPrograms.Trust trust) => trust switch
    {
        TrustedPrograms.Trust.AdminOnly => null,
        TrustedPrograms.Trust.Unknown =>
            $"Who may change {exePath} couldn't be read just now (a file in use?) - the task starts it with administrator " +
            "rights, so it is set up only once that is known. Try again in a moment.",
        _ => $"{exePath} is in a folder that programs without administrator rights can change, and the task would start " +
             "whatever is put there with administrator rights, without asking. Install DNN Manager for all users (its Setup " +
             "does, in Program Files) to start it when you sign in."
    };

    /// <summary>
    /// Registers the task (replacing one that is there) to start <paramref name="exePath"/> when this user signs in -
    /// through the launcher beside it, when there is one.
    /// </summary>
    public async Task<Result> EnableAsync(string exePath, CancellationToken ct = default)
    {
        if (Refusal(exePath) is { } refusal) return Result.Fail(refusal);
        var command = CommandFor(exePath);
        var user = WindowsIdentity.GetCurrent().Name;
        // No time limit (a task is stopped after 3 days by default), also on battery, and never a second instance.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts DNN Manager when {SecurityElement.Escape(user)} signs in (Settings - General in DNN Manager).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <AllowStartOnDemand>false</AllowStartOnDemand>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(command)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(command) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        // Not %TEMP%: schtasks reads it back, and the task it makes runs with the highest rights at every sign-in - the
        // file mustn't be somewhere a program without administrator rights could change it in between.
        var file = Path.Combine(PrivateTemp.Path, $"dnnmanager-startup-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(file, xml, Encoding.Unicode, ct);
            var create = await _process.RunAsync(Schtasks, ["/Create", "/TN", TaskName, "/XML", file, "/F"], ct);
            return create.Success ? Result.Ok() : Result.Fail(Message(create));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ex.Message);
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Removes a task that starts a DNN Manager others could change (one made by 1.8.1 or older for a per-user install,
    /// or for a copy since moved) - with the reason, or null when there is none such. A safe task that starts
    /// DnnManager.exe itself (made before the launcher came with Setup) is made again to start the launcher beside it,
    /// without a word - the task stays as the user set it, only no longer in their .NET environment.
    /// </summary>
    public async Task<string?> RemoveIfUnsafeAsync(CancellationToken ct = default)
    {
        if (!TrustedPrograms.IsElevated || await GetCommandAsync(ct) is not { Length: > 0 } command) return null;
        var target = AppOf(command);
        // Removed only when others can change it for sure: a read that failed (a file in use while Setup or an antivirus
        // has it) is asked once more, and if it still can't be told, the task stays as the user set it - until next start.
        var trust = TrustOf(target, command);
        if (trust == TrustedPrograms.Trust.Unknown)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            trust = TrustOf(target, command);
        }
        if (trust == TrustedPrograms.Trust.Unknown) return null;
        if (trust == TrustedPrograms.Trust.AdminOnly)
        {
            if (!string.Equals(CommandFor(target), command, StringComparison.OrdinalIgnoreCase))
                await EnableAsync(target, ct);
            return null;
        }
        var refusal = RefusalFor(target, trust);
        var removed = await DisableAsync(ct);
        return removed.Success
            ? $"\"Start DNN Manager when you sign in\" is switched off: {refusal}"
            : $"\"Start DNN Manager when you sign in\" should be switched off - {refusal} Removing its scheduled task failed: {removed.Error}";
    }

    /// <summary>Removes the task; fine when there is none.</summary>
    public async Task<Result> DisableAsync(CancellationToken ct = default)
    {
        if (await GetTargetAsync(ct) is null) return Result.Ok();
        var delete = await _process.RunAsync(Schtasks, ["/Delete", "/TN", TaskName, "/F"], ct);
        return delete.Success ? Result.Ok() : Result.Fail(Message(delete));
    }

    private static string Message(ProcessResult result) =>
        (result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim() is { Length: > 0 } text ? text : $"schtasks failed (exit code {result.ExitCode}).";
}
