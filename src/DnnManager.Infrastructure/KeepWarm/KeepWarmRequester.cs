using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Processes;
using Microsoft.Win32.SafeHandles;

namespace DnnManager.Infrastructure.KeepWarm;

/// <summary>
/// Sends keep-warm requests: a plain GET to the site on this machine (<see cref="LocalSiteTarget"/>) - no browser, no
/// proxy, no redirect followed by itself. Each site keeps the cookies it was given, so DNN doesn't hand out a new
/// anonymous cookie (or session) for every request. Only the start of a response is read. A page of DNN's installer is
/// never requested, whatever asks for it.
/// </summary>
internal sealed class KeepWarmRequester : IDisposable
{
    public const string UserAgent = "DnnManager-KeepWarm";

    // Read to the end so the page is rendered whole (and compiled), but not more than this of it.
    private const int MaxBodyBytes = 256 * 1024;
    // What is looked at for DNN's installer.
    private const int BodyStartBytes = 16 * 1024;

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        // Per site, by hand: every site is on 127.0.0.1, and a cookie jar would hand one site's cookies to the others.
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        // Minutes go by between two requests to a site: a connection isn't kept open for the next one.
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
        // A local site's certificate is usually self-signed, or for another name - it is this machine either way.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private readonly object _cookiesLock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _cookies = new(StringComparer.OrdinalIgnoreCase);

    public KeepWarmRequester() => _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

    /// <summary>
    /// Requests <paramref name="path"/> of <paramref name="site"/> - and, for a warm-up, the same site's pages it redirects
    /// to - and says what the answers mean.
    /// </summary>
    public async Task<KeepWarmOutcome> SendAsync(string site, LocalSiteTarget target, string path, KeepWarmRequestKind kind, CancellationToken ct)
    {
        var limit = kind == KeepWarmRequestKind.WarmUp ? KeepWarmRules.WarmUpTimeout : KeepWarmRules.PingTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var watch = Stopwatch.StartNew();
        // In efficiency mode the time measured can be longer than the site took.
        var throttled = PowerThrottling.IsActive;
        TimeSpan? firstAnswer = null;
        var requests = 0;
        var current = path;
        try
        {
            for (var hop = 0; ; hop++)
            {
                // The last safeguard: whatever the settings, a site's own values or a redirect say - checked as asked for
                // and as it would be sent.
                var uri = target.UriFor(current);
                if (KeepWarmSettings.IsInstallerPath(current) || KeepWarmSettings.IsInstallerPath(uri.AbsolutePath))
                    return new KeepWarmOutcome(KeepWarmOutcomeKind.Failed, watch.Elapsed,
                        Reason: $"{current} is a page of DNN's installer - keep warm never requests it", Requests: requests);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Host = target.HostHeader;
                request.Headers.Accept.ParseAdd("text/html,*/*");
                if (CookieHeader(site) is { } cookies) request.Headers.TryAddWithoutValidation("Cookie", cookies);
                requests++;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                firstAnswer ??= watch.Elapsed;
                throttled |= PowerThrottling.IsActive;

                // Windows' HTTP.sys answered, not the site: IIS has no running site for this address - it was stopped
                // (and what DNN Manager knows of it is about to say so), or its app pool is.
                if (response.Headers.Server.Any(p => p.Product?.Name.Equals("Microsoft-HTTPAPI", StringComparison.OrdinalIgnoreCase) == true))
                    return new KeepWarmOutcome(KeepWarmOutcomeKind.NotAnswering, firstAnswer.Value, (int)response.StatusCode,
                        $"nothing in IIS answers for {target.HostHeader} (HTTP {(int)response.StatusCode}) - the site or its app pool may be stopped",
                        Requests: requests, Throttled: throttled);

                KeepCookies(site, response);
                var body = await ReadStartAsync(response, timeout.Token);
                var verdict = KeepWarmRules.Classify(kind, new Uri(target.DisplayUrl(current)), (int)response.StatusCode,
                    response.Headers.Location, body);
                if (verdict.FollowPath is { } next && hop < KeepWarmRules.MaxRedirects)
                {
                    current = next;
                    continue;
                }
                return new KeepWarmOutcome(verdict.Kind, firstAnswer.Value, (int)response.StatusCode, verdict.Reason, verdict.Note, requests,
                    Throttled: throttled);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new KeepWarmOutcome(KeepWarmOutcomeKind.NotAnswering, watch.Elapsed,
                Reason: $"the site didn't answer within {KeepWarmRules.Span(limit)}", Requests: requests, Throttled: throttled, TimedOut: true);
        }
        catch (HttpRequestException ex)
        {
            return new KeepWarmOutcome(KeepWarmOutcomeKind.NotAnswering, watch.Elapsed,
                Reason: ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                    ? $"nothing answers on {target.ConnectHost}:{target.Port}"
                    : $"the site can't be reached: {FirstLine(ex.Message)}",
                Requests: requests, Throttled: throttled);
        }
    }

    /// <summary>Forgets <paramref name="site"/>'s cookies - its worker process is gone, and so are the sessions they were for.</summary>
    public void ForgetCookies(string site)
    {
        lock (_cookiesLock) _cookies.Remove(site);
    }

    private string? CookieHeader(string site)
    {
        lock (_cookiesLock)
            return _cookies.TryGetValue(site, out var jar) && jar.Count > 0
                ? string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}"))
                : null;
    }

    // name=value of each Set-Cookie - its attributes don't matter for one site on one machine; one that is deleted
    // (empty, or expired) goes.
    private void KeepCookies(string site, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        lock (_cookiesLock)
        {
            if (!_cookies.TryGetValue(site, out var jar)) _cookies[site] = jar = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var pair = value.Split(';', 2)[0].Split('=', 2);
                if (pair.Length != 2 || pair[0].Trim().Length == 0) continue;
                var name = pair[0].Trim();
                var deleted = pair[1].Length == 0 ||
                              value.Contains("max-age=0", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("expires=Thu, 01-Jan-1970", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("expires=Mon, 01-Jan-1900", StringComparison.OrdinalIgnoreCase);
                if (deleted) jar.Remove(name);
                // A handful at most, and none of them large.
                else if (pair[1].Length <= 4096 && (jar.ContainsKey(name) || jar.Count < 20)) jar[name] = pair[1].Trim();
            }
        }
    }

    /// <summary>Reads the body to its end (at most <see cref="MaxBodyBytes"/>) and returns its start as text.</summary>
    private static async Task<string> ReadStartAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var start = new MemoryStream();
        var total = 0;
        while (total < MaxBodyBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBodyBytes - total)), ct);
            if (read == 0) break;
            if (start.Length < BodyStartBytes) start.Write(buffer, 0, (int)Math.Min(read, BodyStartBytes - start.Length));
            total += read;
        }
        return Encoding.UTF8.GetString(start.GetBuffer(), 0, (int)start.Length);
    }

    /// <summary>
    /// Whether SQL Server answers on <paramref name="host"/>:<paramref name="port"/> - a TCP connection, nothing more.
    /// A cold DNN can't start without its database, so a warm-up waits for it.
    /// </summary>
    public static async Task<bool> SqlAnswersAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    // ─── Debuggers ────────────────────────────────────────────────────────

    // The programs that debug a site's worker process: Visual Studio (itself, or its remote debugger), the .NET
    // debugger of VS Code and others, Rider's, dnSpy.
    private static readonly string[] DebuggerPrograms = ["devenv", "msvsmon", "vsdbg", "vsdbg-ui", "dnSpy", "dnSpy-x86"];
    private const string RiderDebuggerPrefix = "JetBrains.Debugger.Worker";
    private static readonly TimeSpan DebuggersFreshFor = TimeSpan.FromSeconds(5);
    private static readonly object DebuggersLock = new();
    private static IReadOnlyList<int> _debuggers = [];
    // When _debuggers was read; null before the first time.
    private static long? _debuggersAt;

    /// <summary>
    /// Whether a debugger is attached to one of <paramref name="processIds"/>: a native one (Windows says so), or a
    /// managed one - Visual Studio's usual attach to w3wp, which Windows doesn't report - recognised by a debugger program
    /// that has the process open with the right to write its memory. A request would stop at its breakpoints - in the
    /// middle of someone's debugging session.
    /// </summary>
    public static bool DebuggerAttached(IReadOnlyList<int> processIds)
    {
        if (processIds.Count == 0) return false;
        foreach (var id in processIds)
        {
            using var process = OpenProcess(ProcessQueryInformation, false, id);
            if (process.IsInvalid) continue;
            var present = false;
            if (CheckRemoteDebuggerPresent(process, ref present) && present) return true;
        }
        foreach (var debugger in RunningDebuggers())
            if (HoldsForWriting(debugger, processIds)) return true;
        return false;
    }

    /// <summary>Makes the next <see cref="RunningDebuggers"/> look again (tests).</summary>
    internal static void ForgetDebuggers()
    {
        lock (DebuggersLock) _debuggersAt = null;
    }

    /// <summary>The debugger programs running now (their process IDs) - none on most PCs most of the time.</summary>
    public static IReadOnlyList<int> RunningDebuggers()
    {
        lock (DebuggersLock)
            if (_debuggersAt is { } at && Environment.TickCount64 - at < (long)DebuggersFreshFor.TotalMilliseconds) return _debuggers;
        var found = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var name = process.ProcessName;
                if (DebuggerPrograms.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    name.StartsWith(RiderDebuggerPrefix, StringComparison.OrdinalIgnoreCase))
                    found.Add(process.Id);
            }
        }
        lock (DebuggersLock)
        {
            _debuggers = found;
            _debuggersAt = Environment.TickCount64;
        }
        return found;
    }

    /// <summary>
    /// Whether process <paramref name="holderId"/> has one of <paramref name="processIds"/> open with the right to write
    /// its memory - what a debugger does with the process it debugs. False when that can't be told.
    /// </summary>
    internal static bool HoldsForWriting(int holderId, IReadOnlyList<int> processIds)
    {
        if (ProcessTypeIndex() is not { } processType) return false;
        using var holder = OpenProcess(ProcessQueryInformation | ProcessDupHandle, false, holderId);
        if (holder.IsInvalid) return false;
        foreach (var (handle, access, type) in HandlesOf(holder))
        {
            if (type != processType || (access & ProcessVmWrite) == 0) continue;
            // A copy, only to ask which process it is.
            if (!DuplicateHandle(holder, handle, CurrentProcess, out var copy, ProcessQueryLimitedInformation, false, 0)) continue;
            using (copy)
                if (processIds.Contains(GetProcessId(copy))) return true;
        }
        return false;
    }

    // The kernel's number for "a process" among the kinds of handles - read from a handle of our own.
    private static uint? _processTypeIndex;

    private static uint? ProcessTypeIndex()
    {
        if (_processTypeIndex is { } known) return known;
        using var self = OpenProcess(ProcessQueryLimitedInformation, false, Environment.ProcessId);
        if (self.IsInvalid) return null;
        using var current = new SafeProcessHandle(CurrentProcess, ownsHandle: false);
        var value = self.DangerousGetHandle();
        foreach (var (handle, _, type) in HandlesOf(current))
            if (handle == value) return _processTypeIndex = type;
        return null;
    }

    /// <summary>
    /// The handles <paramref name="process"/> has open: each one's value, granted access and kind
    /// (PROCESS_HANDLE_SNAPSHOT_INFORMATION). Empty when Windows won't tell.
    /// </summary>
    private static List<(IntPtr Handle, uint Access, uint Type)> HandlesOf(SafeProcessHandle process)
    {
        // NumberOfHandles and Reserved (pointer-sized), then the entries: HandleValue, HandleCount and PointerCount
        // (pointer-sized), GrantedAccess, ObjectTypeIndex, HandleAttributes and Reserved (four bytes each).
        var header = 2 * IntPtr.Size;
        var entrySize = 3 * IntPtr.Size + 16;
        var size = 64 * 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(process, ProcessHandleInformation, buffer, size, out var needed);
                if (status is StatusInfoLengthMismatch or StatusBufferTooSmall)
                {
                    size = Math.Max(size * 2, needed + 64 * 1024);
                    continue;
                }
                if (status < 0) return [];
                var count = Marshal.ReadIntPtr(buffer).ToInt64();
                var handles = new List<(IntPtr, uint, uint)>((int)Math.Min(count, 65536));
                for (long i = 0; i < count && header + (i + 1) * entrySize <= size; i++)
                {
                    var entry = buffer + (nint)(header + i * entrySize);
                    handles.Add((Marshal.ReadIntPtr(entry), (uint)Marshal.ReadInt32(entry, 3 * IntPtr.Size),
                        (uint)Marshal.ReadInt32(entry, 3 * IntPtr.Size + 4)));
                }
                return handles;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    private const uint ProcessVmWrite = 0x0020, ProcessDupHandle = 0x0040, ProcessQueryInformation = 0x0400,
        ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessHandleInformation = 51;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004), StatusBufferTooSmall = unchecked((int)0xC0000023);
    private static readonly IntPtr CurrentProcess = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] ref bool debuggerPresent);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out SafeProcessHandle targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(SafeProcessHandle process);

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    public void Dispose() => _http.Dispose();
}
