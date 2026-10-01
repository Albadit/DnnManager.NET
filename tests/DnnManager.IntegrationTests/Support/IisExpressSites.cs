using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// DNN Manager's IIS, played by IIS Express: each site gets a private copy of IIS Express's applicationhost.config and
/// its own iisexpress.exe. Runs as the current user without administrator rights (ports above 1024, bound to
/// localhost) - so Windows authentication means that user, and "the app pool identity" is that user too.
/// </summary>
public sealed class IisExpressSites : IIisManager, IDisposable
{
    public const string InstallDirectory = @"C:\Program Files\IIS Express";

    private readonly string _workDirectory;
    private readonly Dictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);

    public IisExpressSites(string workDirectory) => _workDirectory = workDirectory;

    public static bool Installed => File.Exists(Path.Combine(InstallDirectory, "iisexpress.exe"));

    /// <summary>The Windows account IIS Express - and every site in it - runs as.</summary>
    public static string CurrentUser => $@"{Environment.UserDomainName}\{Environment.UserName}";

    private sealed class Site(string name, string path, string host, int port, string config, string log)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public string Host { get; } = host;
        public int Port { get; } = port;
        public string Config { get; } = config;
        public string Log { get; } = log;
        public Process? Process { get; set; }
        public bool ProfileEnabled { get; set; }
    }

    public Result CreateSite(string siteName, string physicalPath, string hostname, int port)
    {
        RemoveSite(siteName);
        var folder = System.IO.Path.Combine(_workDirectory, "iisexpress", siteName);
        Directory.CreateDirectory(folder);
        var config = System.IO.Path.Combine(folder, "applicationhost.config");
        var doc = XDocument.Load(System.IO.Path.Combine(InstallDirectory, "AppServer", "applicationhost.config"), LoadOptions.PreserveWhitespace);
        var sites = doc.Root!.Element("system.applicationHost")!.Element("sites")!;
        sites.Elements("site").Remove();
        sites.AddFirst(new XElement("site",
            new XAttribute("name", siteName), new XAttribute("id", 1), new XAttribute("serverAutoStart", "true"),
            new XElement("application", new XAttribute("path", "/"), new XAttribute("applicationPool", "Clr4IntegratedAppPool"),
                new XElement("virtualDirectory", new XAttribute("path", "/"), new XAttribute("physicalPath", System.IO.Path.GetFullPath(physicalPath)))),
            new XElement("bindings",
                new XElement("binding", new XAttribute("protocol", "http"), new XAttribute("bindingInformation", $"*:{port}:{hostname}")))));
        var defaults = sites.Element("siteDefaults");
        defaults?.Element("logFile")?.SetAttributeValue("directory", System.IO.Path.Combine(folder, "logs"));
        defaults?.Element("traceFailedRequestsLogging")?.SetAttributeValue("directory", System.IO.Path.Combine(folder, "trace"));
        doc.Save(config);
        _sites[siteName] = new Site(siteName, physicalPath, hostname, port, config, System.IO.Path.Combine(folder, "iisexpress.log"));
        return Result.Ok();
    }

    public Result RemoveSite(string siteName)
    {
        if (!_sites.TryGetValue(siteName, out var site)) return Result.Ok();
        Stop(site);
        _sites.Remove(siteName);
        return Result.Ok();
    }

    public Result StartSite(string siteName)
    {
        if (!_sites.TryGetValue(siteName, out var site)) return Result.Fail($"No site '{siteName}'.");
        if (site.Process is { HasExited: false }) return Result.Ok();
        try
        {
            Start(site);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex.Message);
        }
    }

    public Result StopSite(string siteName)
    {
        if (_sites.TryGetValue(siteName, out var site)) Stop(site);
        return Result.Ok();
    }

    public Result RestartSite(string siteName)
    {
        StopSite(siteName);
        return StartSite(siteName);
    }

    // IIS Express has no app pool of its own to recycle: a restart is the same for the site.
    public Result RecycleAppPool(string siteName) =>
        _sites.TryGetValue(siteName, out var site) && site.Process is { HasExited: false } ? RestartSite(siteName) : Result.Ok();

    public string? GetLogDirectory(string siteName) => null;

    public string AppPoolIdentity(string siteName) => CurrentUser;

    public Result EnableUserProfile(string siteName)
    {
        if (_sites.TryGetValue(siteName, out var site)) site.ProfileEnabled = true;
        return Result.Ok();
    }

    /// <summary>Whether <see cref="EnableUserProfile"/> was asked for the site - what a LocalDB database needs under IIS.</summary>
    public bool ProfileEnabled(string siteName) => _sites.TryGetValue(siteName, out var site) && site.ProfileEnabled;

    public bool IsAvailable() => Installed;

    public IisServerState GetServerState() => IisServerState.Running;

    public Task<Result> ControlServerAsync(IisServerAction action, CancellationToken ct) => Task.FromResult(Result.Ok());

    public IReadOnlyDictionary<string, string> GetSiteStates() =>
        _sites.Values.ToDictionary(s => s.Name, s => s.Process is { HasExited: false } ? "Started" : "Stopped", StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IisSiteRuntime>? GetSiteRuntimes() => new Dictionary<string, IisSiteRuntime>();

    public IReadOnlyDictionary<string, SiteTraffic> GetSiteTraffic() => new Dictionary<string, SiteTraffic>();

    public IisSiteInfo? GetSiteInfo(string siteName) => null;

    public Result GrantPermissions(string path, IEnumerable<string> identities) => Result.Ok();

    public Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct) => Task.FromResult(Result.Ok());

    public void Dispose()
    {
        foreach (var site in _sites.Values) Stop(site);
    }

    // ─── iisexpress.exe ───────────────────────────────────────────────────

    private static void Start(Site site)
    {
        if (PortOpen(site.Port)) throw new InvalidOperationException($"Port {site.Port} is in use.");
        var start = new ProcessStartInfo(System.IO.Path.Combine(InstallDirectory, "iisexpress.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add($"/config:{site.Config}");
        start.ArgumentList.Add($"/site:{site.Name}");
        start.ArgumentList.Add("/systray:false");

        var running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Output(string? line)
        {
            if (line is null) return;
            lock (site) File.AppendAllText(site.Log, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            if (line.Contains("IIS Express is running", StringComparison.OrdinalIgnoreCase)) running.TrySetResult(true);
        }
        process.OutputDataReceived += (_, e) => Output(e.Data);
        process.ErrorDataReceived += (_, e) => Output(e.Data);
        process.Exited += (_, _) => running.TrySetResult(false);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        site.Process = process;

        if (!running.Task.Wait(TimeSpan.FromSeconds(60)) || !running.Task.Result)
        {
            if (!process.HasExited) process.Kill();
            throw new InvalidOperationException($"IIS Express didn't start - see {site.Log}");
        }
        var waited = Stopwatch.StartNew();
        while (!PortOpen(site.Port) && waited.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(200);
    }

    /// <summary>
    /// Stops the site's iisexpress.exe the way Visual Studio does: WM_QUIT to its threads (it ignores 'Q' on a redirected
    /// input), so ASP.NET shuts the site down cleanly; killed only if it doesn't go.
    /// </summary>
    private static void Stop(Site site)
    {
        var process = site.Process;
        if (process is null) return;
        if (!process.HasExited)
        {
            try
            {
                process.Refresh();
                foreach (ProcessThread thread in process.Threads) PostThreadMessage((uint)thread.Id, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }
            catch (InvalidOperationException) { }
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                process.WaitForExit();
            }
        }
        process.Dispose();
        site.Process = null;
        var waited = Stopwatch.StartNew();
        while (PortOpen(site.Port) && waited.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(200);
    }

    public static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(300) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    private const uint WmQuit = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}