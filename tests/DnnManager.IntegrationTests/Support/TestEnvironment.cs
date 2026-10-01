using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// What the integration tests run on, all of it their own: a folder under <c>%LOCALAPPDATA%\DnnManagerTests</c>, the DNN
/// install package (downloaded once), a LocalDB instance and a Docker SQL Server container made for the run and
/// removed after it. Never the user's projects, settings, <c>MSSQLLocalDB</c> instance or SQL container.
/// </summary>
public static class TestEnvironment
{
    public const string DnnVersion = "10.3.3";
    public const string PackageUrl = $"https://github.com/dnnsoftware/Dnn.Platform/releases/download/v{DnnVersion}/DNN_Platform_{DnnVersion}_Install.zip";

    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnnManagerTests");

    /// <summary>A new folder for one test run - short, as DNN's packages need a short site path.</summary>
    public static string NewRunDirectory()
    {
        var run = Path.Combine(Root, Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant());
        Directory.CreateDirectory(run);
        return run;
    }

    /// <summary>
    /// DNN's install package: <c>DNNMANAGER_TEST_DNN_ZIP</c> when set, else the one downloaded before, else downloaded
    /// now (about 42 MB).
    /// </summary>
    public static async Task<string> DnnPackageAsync()
    {
        if (Environment.GetEnvironmentVariable("DNNMANAGER_TEST_DNN_ZIP") is { Length: > 0 } given && File.Exists(given)) return given;
        var cached = Path.Combine(Root, "cache", $"DNN_Platform_{DnnVersion}_Install.zip");
        if (File.Exists(cached)) return cached;
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DnnManager-IntegrationTests");
        var download = cached + ".download";
        await using (var target = File.Create(download))
        await using (var source = await http.GetStreamAsync(PackageUrl))
            await source.CopyToAsync(target);
        File.Move(download, cached);
        return cached;
    }

    /// <summary>A port nobody listens on, for a site.</summary>
    public static int FreePort()
    {
        for (var port = 8120; port < 8200; port++)
        {
            if (IisExpressSites.PortOpen(port)) continue;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        throw new InvalidOperationException("No free port between 8120 and 8199.");
    }

    public static string Password(int length = 20, string symbols = "!%*-_&'\"") =>
        string.Concat("Aa1" + symbols[..2] + Convert.ToBase64String(RandomNumberGenerator.GetBytes(length)).Replace("/", "x").Replace("+", "y"))[..length];

    // ─── LocalDB ──────────────────────────────────────────────────────────

    /// <summary>SqlLocalDB.exe of the newest LocalDB installed, or null when there is none.</summary>
    public static string? SqlLocalDb()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");
        if (key is null) return null;
        foreach (var version in key.GetSubKeyNames().OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version()))
        {
            var major = version.Split('.')[0];
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server", major + "0", "Tools", "Binn", "SqlLocalDB.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Creates and starts LocalDB instance <paramref name="name"/>; returns its server name, e.g. <c>(localdb)\dnnit_ab12</c>.</summary>
    public static string CreateLocalDbInstance(string name)
    {
        var exe = SqlLocalDb() ?? throw new InvalidOperationException("LocalDB isn't installed.");
        Run(exe, $"create \"{name}\" -s");
        return $@"(localdb)\{name}";
    }

    public static void DeleteLocalDbInstance(string name)
    {
        if (SqlLocalDb() is not { } exe) return;
        Run(exe, $"stop \"{name}\" -k", check: false);
        Run(exe, $"delete \"{name}\"", check: false);
    }

    // ─── Docker ───────────────────────────────────────────────────────────

    public const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    public static bool DockerAvailable()
    {
        try
        {
            return Run("docker", "version --format {{.Server.Version}}", check: false).ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a SQL Server container of its own on <paramref name="port"/> with <paramref name="saPassword"/> and waits until
    /// sa can sign in. Returns its server name, e.g. <c>127.0.0.1,14399</c>.
    /// </summary>
    public static async Task<string> StartSqlContainerAsync(string name, int port, string saPassword)
    {
        Run("docker", $"run -d --name {name} -e ACCEPT_EULA=Y -e \"MSSQL_SA_PASSWORD={saPassword}\" -p 127.0.0.1:{port}:1433 {SqlServerImage}");
        var server = $"127.0.0.1,{port}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = "master", UserID = "sa", Password = saPassword,
            Encrypt = true, TrustServerCertificate = true, ConnectTimeout = 5, Pooling = false
        };
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync();
                return server;
            }
            catch (SqlException) when (waited.Elapsed < TimeSpan.FromSeconds(120))
            {
                await Task.Delay(2000);
            }
        }
    }

    public static void RemoveContainer(string name) => Run("docker", $"rm -f {name}", check: false);

    // ─── Helpers ──────────────────────────────────────────────────────────

    public static (int ExitCode, string Output) Run(string exe, string arguments, bool check = true)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (check && process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} {arguments.Split(' ')[0]} failed: {output}");
        return (process.ExitCode, output);
    }

    /// <summary>Deletes a test folder, retrying while LocalDB or IIS Express still let go of files.</summary>
    public static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(directory); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(1000);
            }
        }
    }
}