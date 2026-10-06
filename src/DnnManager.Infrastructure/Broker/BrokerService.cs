using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using DnnManager.Infrastructure.Iis;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using static DnnManager.Infrastructure.Broker.BrokerProtocol;

namespace DnnManager.Infrastructure.Broker;

/// <summary>
/// <c>DnnManager.exe --broker</c>: the same exe as the DNN Manager Broker Windows service, plus the commands to set it up
/// and try it from a terminal (see <see cref="Run"/>).
/// </summary>
public sealed class BrokerService : ServiceBase
{
    public const string Argument = "--broker";

    /// <summary>
    /// Where <c>--broker install</c> puts the service's copy of DNN Manager: a folder only administrators can change. A
    /// service running as Local System from the user's own install folder (<c>%LOCALAPPDATA%\Programs</c>) would let
    /// anything that runs as the user become Local System by replacing the exe.
    /// </summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DnnManager", "Broker");

    private CancellationTokenSource? _stop;
    private Task? _serving;
    private ILoggerFactory? _logs;

    private BrokerService()
    {
        ServiceName = BrokerProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    public static bool IsBroker(string[] args) => args.Length > 0 && args[0] == Argument;

    /// <summary>
    /// <c>--broker</c> runs as the service (the Service Control Manager starts it so); <c>--broker install</c> and
    /// <c>uninstall</c> set it up and remove it (in a terminal run as Administrator); <c>--broker call &lt;operation&gt;
    /// [site]</c> sends one request and prints the answer - <c>call ping</c>, <c>call sites.states</c>,
    /// <c>call site.stop mysite</c>.
    /// </summary>
    public static int Run(string[] args)
    {
        // Started by the Service Control Manager: no desktop to interact with.
        if (args.Length == 1 && !Environment.UserInteractive)
        {
            ServiceBase.Run(new BrokerService());
            return 0;
        }

        ConsoleOutput.Attach();
        if (args.Length == 1)
        {
            Console.Error.WriteLine($"{Argument} is started by Windows as the {DisplayName} service.");
            return Usage();
        }
        try
        {
            return args[1] switch
            {
                "install" => Install(),
                "uninstall" => Uninstall(),
                "call" when args.Length >= 3 => Call(args[2], args.Length >= 4 ? args[3] : null),
                _ => Usage()
            };
        }
        catch (Exception ex) when (ex is BrokerUnavailableException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // ─── The service ──────────────────────────────────────────────────────

    protected override void OnStart(string[] args)
    {
        _logs = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddEventLog(new EventLogSettings { SourceName = BrokerProtocol.ServiceName, LogName = "Application" }));
        var log = _logs.CreateLogger(BrokerProtocol.ServiceName);
        var server = new BrokerServer(new IisManager(new ProcessRunner(), _logs.CreateLogger<IisManager>()), log);
        _stop = new CancellationTokenSource();
        _serving = Task.Run(async () =>
        {
            try { await server.RunAsync(_stop.Token); }
            catch (Exception ex)
            {
                // E.g. another process made the pipe first: the service can't do its work - say why and stop.
                log.LogError(ex, "The {Service} stopped answering", DisplayName);
                Stop();
            }
        });
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stop?.Cancel();
        try { _serving?.Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { /* logged where it happened */ }
        _logs?.Dispose();
    }

    // ─── The commands ─────────────────────────────────────────────────────

    private static int Install()
    {
        if (!IsAdministrator()) return NeedsAdministrator("install");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("This program's path isn't known.");
        var target = InstallFolder;

        using (var existing = Find())
        {
            if (existing is not null && existing.Status != ServiceControllerStatus.Stopped)
            {
                Console.WriteLine($"Stopping the {DisplayName}...");
                existing.Stop();
                existing.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
        }

        // This DNN Manager's files (a single-file exe and its native DLLs, or a build's output) - not an uninstaller.
        Console.WriteLine($"Copying {Path.GetDirectoryName(exe)} to {target}...");
        CopyFolder(Path.GetDirectoryName(exe)!, target);
        var serviceExe = Path.Combine(target, Path.GetFileName(exe));

        var binPath = $"\"{serviceExe}\" {Argument}";
        if (ServiceExists())
            Sc("config", BrokerProtocol.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
        else
            Sc("create", BrokerProtocol.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", DisplayName);
        Sc("description", BrokerProtocol.ServiceName, "Starts, stops and reads IIS sites for DNN Manager when it runs without Administrator rights (experiment).");

        using var service = new ServiceController(BrokerProtocol.ServiceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        Console.WriteLine($"The {DisplayName} runs ({serviceExe}).");
        return 0;
    }

    private static int Uninstall()
    {
        if (!IsAdministrator()) return NeedsAdministrator("uninstall");
        using (var service = Find())
        {
            if (service is null)
            {
                Console.WriteLine($"The {DisplayName} isn't installed.");
            }
            else
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
                Sc("delete", BrokerProtocol.ServiceName);
                Console.WriteLine($"The {DisplayName} is removed.");
            }
        }
        var running = Path.GetFullPath(Environment.ProcessPath ?? "");
        if (running.StartsWith(Path.GetFullPath(InstallFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            // This exe can't delete the folder it runs from.
            Console.WriteLine($"Delete {InstallFolder} once this has exited.");
        }
        else if (Directory.Exists(InstallFolder))
        {
            Directory.Delete(InstallFolder, recursive: true);
            var parent = Path.GetDirectoryName(InstallFolder)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        return 0;
    }

    private static int Call(string operation, string? site)
    {
        var response = new BrokerClient().Send(BrokerRequest.For(operation, site), TimeSpan.FromMinutes(3));
        Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions(Json) { WriteIndented = true }));
        return response.Success ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine($"DnnManager.exe {Argument} install | uninstall | call <operation> [site]");
        Console.Error.WriteLine($"  operations: {string.Join(", ", typeof(Operations).GetFields().Select(f => f.GetValue(null)))}");
        return 2;
    }

    private static int NeedsAdministrator(string command)
    {
        Console.Error.WriteLine($"{Argument} {command} needs Administrator rights - run it in a terminal opened as Administrator.");
        return 5;
    }

    private static ServiceController? Find() =>
        ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(BrokerProtocol.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static bool ServiceExists()
    {
        using var service = Find();
        return service is not null;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (Path.GetFileName(file).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void Sc(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var sc = Process.Start(start)!;
        var output = sc.StandardOutput.ReadToEnd() + sc.StandardError.ReadToEnd();
        sc.WaitForExit();
        if (sc.ExitCode != 0) throw new InvalidOperationException($"sc {args[0]} failed (exit code {sc.ExitCode}): {output.Trim()}");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Console output for a command of this windowed exe: the terminal it was started from, or where it was redirected.</summary>
internal static class ConsoleOutput
{
    private const int StdOutput = -11, AttachParentProcess = -1;

    public static void Attach()
    {
        // Redirected (piped, or written to a file): the handle is there already.
        if (GetStdHandle(StdOutput) is var handle && handle != IntPtr.Zero && handle != new IntPtr(-1)) return;
        AttachConsole(AttachParentProcess);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
