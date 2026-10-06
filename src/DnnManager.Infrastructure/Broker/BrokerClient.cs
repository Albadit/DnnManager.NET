using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using static DnnManager.Infrastructure.Broker.BrokerProtocol;

namespace DnnManager.Infrastructure.Broker;

/// <summary>
/// DNN Manager's side of the pipe: sends a request to the DNN Manager Broker service and returns its answer - after
/// checking that the pipe's server is the service's own process, not another one that made a pipe of the same name.
/// </summary>
public sealed class BrokerClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The broker service runs on this PC.</summary>
    public static bool IsServiceRunning => ServiceProcessId() != 0;

    /// <summary>Sends <paramref name="request"/> and waits up to <paramref name="timeout"/> for the answer.</summary>
    /// <exception cref="BrokerUnavailableException">The service doesn't run, or didn't answer.</exception>
    public BrokerResponse Send(BrokerRequest request, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // The IIS interface the broker stands in for is synchronous; nothing below captures a context.
            return SendAsync(request, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            throw new BrokerUnavailableException($"The {DisplayName} didn't answer within {timeout.TotalSeconds:0} seconds.", ex);
        }
    }

    public async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken ct)
    {
        var servicePid = ServiceProcessId();
        if (servicePid == 0) throw new BrokerUnavailableException($"The {DisplayName} service isn't running.");

        // Identification: the service may read who is asking, not act as the caller.
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new BrokerUnavailableException($"The {DisplayName} service runs but doesn't answer on its pipe.", ex);
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid))
            throw new BrokerUnavailableException($"Couldn't tell who answers on the {DisplayName}'s pipe.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        if (serverPid != servicePid)
            throw new BrokerUnavailableException(
                $"Process {serverPid} answers on the {DisplayName}'s pipe, not the service (process {servicePid}) - nothing was sent to it.");

        try
        {
            await WriteAsync(pipe, request, ct).ConfigureAwait(false);
            return await ReadAsync<BrokerResponse>(pipe, MaxResponseBytes, ct).ConfigureAwait(false)
                   ?? throw new BrokerUnavailableException($"The {DisplayName} closed the connection without an answer.");
        }
        catch (IOException ex)
        {
            throw new BrokerUnavailableException($"The connection to the {DisplayName} broke: {ex.Message}", ex);
        }
    }

    /// <summary>The broker service's process ID; 0 when it isn't installed or isn't running.</summary>
    public static uint ServiceProcessId()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return 0;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return 0;
            try
            {
                var status = new ServiceStatusProcess();
                return QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
                       && status.CurrentState == ServiceRunning
                    ? status.ProcessId
                    : 0;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ServiceRunning = 4;
    private const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint,
            ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);
}
