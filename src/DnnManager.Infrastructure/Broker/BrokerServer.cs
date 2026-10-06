using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using static DnnManager.Infrastructure.Broker.BrokerProtocol;

namespace DnnManager.Infrastructure.Broker;

/// <summary>
/// The broker service's side of the pipe: it answers each request with the real IIS (<paramref name="iis"/>, which needs
/// the service's rights) - but only for an administrator of this PC, and only the few operations it knows.
/// </summary>
public sealed class BrokerServer(IIisManager iis, ILogger log)
{
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string NetworkSid = "S-1-5-2";

    /// <summary>Requests answered at the same time; a fifth caller waits for a free instance of the pipe.</summary>
    private const int MaxInstances = 4;

    /// <summary>How long a caller has to send its request once it is connected.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IIisManager _iis = iis;
    private readonly ILogger _log = log;

    /// <summary>Answers requests until <paramref name="ct"/> is cancelled, then waits for the ones being answered.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var free = new SemaphoreSlim(MaxInstances);
        var serving = new List<Task>();
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            try { await free.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // The first instance must be the pipe's first: when another process made a pipe of this name before the
            // service started, the service doesn't answer on it - a DNN Manager would be talking to that process.
            // (DNN Manager checks too: it only talks to the pipe whose server is the service's process.)
            var pipe = CreatePipe(first);
            first = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                free.Release();
                break;
            }

            lock (serving)
            {
                serving.RemoveAll(t => t.IsCompleted);
                serving.Add(Task.Run(async () =>
                {
                    try { await ServeAsync(pipe, ct).ConfigureAwait(false); }
                    finally { free.Release(); }
                }, CancellationToken.None));
            }
        }

        Task[] left;
        lock (serving) left = serving.ToArray();
        await Task.WhenAll(left).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Signed in at this PC: may connect, read and write - but not create instances of the pipe (ReadWrite has no
        // CreateNewInstance), so no one else can answer on it while the service runs.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Never over the network.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            options, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            BrokerResponse response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                var request = await ReadAsync<BrokerRequest>(pipe, MaxRequestBytes, timeout.Token).ConfigureAwait(false);
                if (request is null) return;

                // Who is asking: the caller's token, read while impersonating it (DNN Manager connects with the
                // Identification level - enough to read its groups, not to act as it).
                WindowsIdentity? caller = null;
                pipe.RunAsClient(() => caller = WindowsIdentity.GetCurrent(TokenAccessLevels.Query));
                using (caller)
                {
                    var refusal = Refusal(caller!.Groups?.Select(g => g.Value) ?? [],
                        caller.Claims.Where(c => c.Type == ClaimTypes.DenyOnlySid).Select(c => c.Value));
                    if (refusal is not null)
                    {
                        _log.LogWarning("Refused {Operation} for {Caller}: {Reason}", request.Operation, caller.Name, refusal);
                        response = BrokerResponse.Fail(refusal);
                    }
                    else
                    {
                        response = Handle(request);
                        _log.LogInformation("{Caller}: {Operation} {Site} - {Outcome}", caller.Name, request.Operation,
                            request.Site ?? "", response.Success ? "done" : response.Error);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
                                           or OperationCanceledException or UnauthorizedAccessException)
            {
                // A caller that sent nothing, too much or something that isn't a request: no answer.
                _log.LogWarning("A request could not be read: {Reason}", ex.Message);
                return;
            }

            try { await WriteAsync(pipe, response, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { /* the caller has gone */ }
        }
    }

    /// <summary>
    /// Null when a caller with these groups may use the broker; else why not. An administrator of this PC may - also
    /// when it runs without its Administrator rights (UAC's filtered token has the Administrators group as deny-only):
    /// that is the point of the broker. Anyone else, and anyone over the network, may not.
    /// </summary>
    public static string? Refusal(IEnumerable<string> groupSids, IEnumerable<string> denyOnlyGroupSids)
    {
        var groups = groupSids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Contains(NetworkSid)) return "The DNN Manager Broker doesn't answer over the network.";
        if (groups.Contains(AdministratorsSid) || denyOnlyGroupSids.Contains(AdministratorsSid, StringComparer.OrdinalIgnoreCase))
            return null;
        return "Only an administrator of this PC may use the DNN Manager Broker.";
    }

    /// <summary>Does what <paramref name="request"/> asks - for a caller that may ask.</summary>
    public BrokerResponse Handle(BrokerRequest request)
    {
        if (request.Version != BrokerProtocol.Version)
            return BrokerResponse.Fail($"DNN Manager speaks version {request.Version} of the broker's protocol and the " +
                                       $"DNN Manager Broker service version {BrokerProtocol.Version} - install the same release of both.");

        switch (request.Operation)
        {
            case Operations.Ping:
                return BrokerResponse.Ok(BrokerProtocol.Version);
            case Operations.SiteStates:
                return BrokerResponse.Ok(_iis.GetSiteStates());
            case Operations.SiteRuntimes:
                return _iis.GetSiteRuntimes() is { } runtimes
                    ? BrokerResponse.Ok(runtimes)
                    : BrokerResponse.Fail("IIS's configuration couldn't be read.");
        }

        if (!IsSiteName(request.Site)) return BrokerResponse.Fail("The request has no valid site name.");
        var site = request.Site!;
        Result? result = request.Operation switch
        {
            Operations.StartSite => _iis.StartSite(site),
            Operations.StopSite => _iis.StopSite(site),
            Operations.StopSiteAndWait => _iis.StopSiteAndWait(site, TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? 60, 1, 300))),
            Operations.RestartSite => _iis.RestartSite(site),
            _ => null
        };
        if (result is null) return BrokerResponse.Fail($"The DNN Manager Broker doesn't know the operation '{request.Operation}'.");
        return result.Success ? BrokerResponse.Ok() : BrokerResponse.Fail(result.Error ?? $"{request.Operation} failed.");
    }

    private static bool IsSiteName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);
}
