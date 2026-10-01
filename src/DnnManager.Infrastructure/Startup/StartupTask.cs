using System.Security;
using System.Security.Principal;
using System.Text;
using DnnManager.Domain;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Startup;

/// <summary>
/// "Start DNN Manager when you sign in": a scheduled task that starts it at the user's logon with the highest
/// privileges. A plain startup entry (the Run registry key, or the Startup folder) would ask for Administrator rights
/// with a UAC prompt at every sign-in, since DNN Manager runs elevated; a task registered while elevated doesn't.
/// </summary>
public sealed class StartupTask
{
    public const string TaskName = "DNN Manager";

    private readonly ProcessRunner _process;

    public StartupTask(ProcessRunner process) => _process = process;

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>The exe the task starts, or null when there is no task.</summary>
    public async Task<string?> GetTargetAsync(CancellationToken ct = default)
    {
        var query = await _process.RunAsync(Schtasks, ["/Query", "/TN", TaskName, "/XML"], ct);
        if (!query.Success) return null;
        // <Command>C:\…\DnnManager.exe</Command>
        const string open = "<Command>", close = "</Command>";
        var start = query.StdOut.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : query.StdOut.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? "" : System.Net.WebUtility.HtmlDecode(query.StdOut[(start + open.Length)..end]).Trim().Trim('"');
    }

    /// <summary>Registers the task (replacing one that is there) to start <paramref name="exePath"/> when this user signs in.</summary>
    public async Task<Result> EnableAsync(string exePath, CancellationToken ct = default)
    {
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
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var file = Path.Combine(Path.GetTempPath(), $"dnnmanager-startup-{Guid.NewGuid():N}.xml");
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
