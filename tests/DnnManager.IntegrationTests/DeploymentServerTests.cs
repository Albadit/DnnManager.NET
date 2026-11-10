using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Infrastructure.Deployment;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Push, pull and rollback against real servers in Docker: OpenSSH with a shell (the server hashes files with
/// sha256sum), an SFTP-only account in a chroot (files read back to hash them), vsftpd over plain FTP and over FTPS with
/// a self-signed certificate. Each server's identity must be trusted first. Inconclusive without Docker (Linux containers).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DeploymentServerTests
{
    private const string User = "deploy";
    private const string Password = "S3cret-deploy";
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..6];
    private static readonly string Ssh = $"dnnmanager-test-ssh-{Suffix}";
    private static readonly string Chroot = $"dnnmanager-test-sftp-{Suffix}";
    private static readonly string Ftp = $"dnnmanager-test-ftp-{Suffix}";
    private static readonly string Ftps = $"dnnmanager-test-ftps-{Suffix}";
    private static readonly List<string> Containers = [];
    private static string? _certFolder;

    [ClassInitialize]
    public static async Task StartServers(TestContext _)
    {
        if (!TestEnvironment.DockerAvailable()) return;
        // OpenSSH with a shell: the account may run sha256sum.
        Start(Ssh, $"-e PUID=1000 -e PGID=1000 -e USER_NAME={User} -e USER_PASSWORD={Password} -e PASSWORD_ACCESS=true -p 127.0.0.1::2222 lscr.io/linuxserver/openssh-server:latest");
        // SFTP only, in a chroot: no commands - files are read back.
        Start(Chroot, $"-p 127.0.0.1::22 atmoz/sftp:alpine {User}:{Password}:1001::www");
        Start(Ftp, $"-e \"USERS={User}|{Password}|/home/{User}\" -e ADDRESS=127.0.0.1 -e MIN_PORT=21600 -e MAX_PORT=21609 " +
                   "-p 127.0.0.1:21021:21 -p 127.0.0.1:21600-21609:21600-21609 delfer/alpine-ftp-server:latest");
        // FTPS with a certificate nobody issued - trusted by its fingerprint, as on most hosting.
        _certFolder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dnnmanager-ftps-" + Suffix)).FullName;
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=ftps.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(Path.Combine(_certFolder, "cert.pem"), cert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(_certFolder, "key.pem"), rsa.ExportPkcs8PrivateKeyPem());
        }
        Start(Ftps, $"-e \"USERS={User}|{Password}|/home/{User}\" -e ADDRESS=127.0.0.1 -e MIN_PORT=21700 -e MAX_PORT=21709 " +
                    $"-e TLS_CERT=/certs/cert.pem -e TLS_KEY=/certs/key.pem -v \"{_certFolder}:/certs:ro\" " +
                    "-p 127.0.0.1:21022:21 -p 127.0.0.1:21700-21709:21700-21709 delfer/alpine-ftp-server:latest");

        await WaitForPort(Port(Ssh, 2222));
        await WaitForPort(Port(Chroot, 22));
        await WaitForBanner(21021);
        await WaitForBanner(21022);
        // The SSH image makes its user at start; the project's folder on it is made here.
        for (var i = 0; i < 30 && TestEnvironment.Run("docker", $"exec {Ssh} sh -c \"id {User}\"", check: false).ExitCode != 0; i++) await Task.Delay(1000);
        TestEnvironment.Run("docker", $"exec {Ssh} sh -c \"mkdir -p /config/www && chown {User} /config/www\"");
        await Task.Delay(2000);
    }

    [ClassCleanup]
    public static void StopServers()
    {
        foreach (var name in Containers) TestEnvironment.RemoveContainer(name);
        if (_certFolder is not null) TestEnvironment.DeleteDirectory(_certFolder);
    }

    private static void Start(string name, string arguments)
    {
        TestEnvironment.Run("docker", $"run -d --name {name} {arguments}");
        Containers.Add(name);
    }

    private static int Port(string container, int inside)
    {
        var output = TestEnvironment.Run("docker", $"port {container} {inside}/tcp").Output.Trim().Split('\n')[0];
        return int.Parse(output[(output.LastIndexOf(':') + 1)..]);
    }

    private static async Task WaitForPort(int port)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(1000);
            }
        }
        Assert.Fail($"Nothing answers on port {port}.");
    }

    /// <summary>Until vsftpd greets (220) - its port answers before it does, and a test signing in then fails.</summary>
    private static async Task WaitForBanner(int port)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var reader = new StreamReader(client.GetStream());
                var read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(2000)) == read && (await read)?.StartsWith("220") == true) return;
            }
            catch (Exception ex) when (ex is SocketException or IOException) { }
            await Task.Delay(1000);
        }
        Assert.Fail($"No FTP server greets on port {port}.");
    }

    private static DeployConnection Connection(string server) => server switch
    {
        "ssh" => new DeployConnection { Site = "shop", Name = "Live", Host = "127.0.0.1", Port = Port(Ssh, 2222), UserName = User, RemoteRoot = "/config/www", Parallel = 3 },
        "chroot" => new DeployConnection { Site = "shop", Name = "Live", Host = "127.0.0.1", Port = Port(Chroot, 22), UserName = User, RemoteRoot = "/www", Parallel = 3 },
        "ftp" => new DeployConnection { Site = "shop", Name = "Live", Protocol = TransferProtocol.Ftp, Encryption = FtpEncryption.PlainOnly, Host = "127.0.0.1", Port = 21021, UserName = User, RemoteRoot = "/home/deploy", Parallel = 2 },
        _ => new DeployConnection { Site = "shop", Name = "Live", Protocol = TransferProtocol.Ftp, Encryption = FtpEncryption.RequireExplicit, Host = "127.0.0.1", Port = 21022, UserName = User, RemoteRoot = "/home/deploy", Parallel = 2 }
    };

    /// <summary>The server's identity, trusted after a first connection shows it - as the connection dialog asks the user.</summary>
    private static async Task<DeployConnection> TrustedAsync(DeployConnection connection)
    {
        if (connection.IsPlainFtp) return connection;
        var store = new DeployConnections(new DeployHarness().Store, new RemoteFileSystemFactory());
        var first = await store.TestAsync(connection, new DeploySecrets(Password), CancellationToken.None);
        Assert.IsTrue(first.NeedsTrust, first.Message);
        Assert.IsFalse(first.IdentityChanged);
        StringAssert.StartsWith(first.Identity, "SHA256:");
        var trusted = connection with { TrustedHostKey = first.Identity };
        var second = await store.TestAsync(trusted, new DeploySecrets(Password), CancellationToken.None);
        Assert.IsTrue(second.Ok, second.Message);
        return trusted;
    }

    private static void RequireDocker()
    {
        if (!TestEnvironment.DockerAvailable()) Assert.Inconclusive("Docker with Linux containers isn't running.");
    }

    [TestMethod]
    [DataRow("ssh")]
    [DataRow("chroot")]
    [DataRow("ftp")]
    [DataRow("ftps")]
    public async Task Push_pull_and_rollback_work_against_a_real_server(string server)
    {
        RequireDocker();
        var connection = await TrustedAsync(Connection(server));
        using var h = new DeployHarness(connection with { IgnoreRules = IgnoreRules.Defaults }, new DeploySecrets(Password), new RemoteFileSystemFactory());

        // A new project: everything is to push.
        h.WriteLocal("index.html", "<h1>shop</h1>");
        h.WriteLocal("Portals/0/site.css", "body { color: red }");
        h.WriteLocal("bin/Shop.dll", new string('b', 300_000));
        h.WriteLocal("web.config", "<local only/>");
        var first = await h.ScanAsync();
        Assert.AreEqual(3, first.Count(SyncDirection.Push), h.Reporter.Text);
        var pushed = await h.PushAsync(first, [.. first.Changes.Select(c => c.Path)]);
        Assert.IsTrue(pushed.Success, pushed.Error + Environment.NewLine + h.Reporter.Text);

        await using (var fs = await h.OpenServerAsync())
        {
            Assert.AreEqual(300_000, (await fs.StatAsync("bin/Shop.dll", CancellationToken.None))!.Size);
            Assert.IsNull(await fs.StatAsync("web.config", CancellationToken.None), "An ignored file isn't sent.");
            Assert.IsNull(await fs.StatAsync(DeployPath.LockFileName, CancellationToken.None), "The lock is let go of.");
        }
        var synced = await h.ScanAsync();
        Assert.IsTrue(synced.Items.All(i => i.Status == SyncStatus.Synchronized), h.Reporter.Text);
        Assert.AreEqual(0, synced.Fetched, "An unchanged server isn't read again.");

        // Someone edits the live site with their own client.
        await Task.Delay(1100);
        await using (var fs = await h.OpenServerAsync())
        {
            await fs.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("body { color: blue; } /* hotfix */")), "Portals/0/site.css", 0, null, CancellationToken.None);
            await fs.UploadAsync(new MemoryStream("live only"u8.ToArray()), "added-live.txt", 0, null, CancellationToken.None);
        }
        var live = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.RemoteModified, live.Find("Portals/0/site.css")!.Status, h.Reporter.Text);
        Assert.AreEqual(SyncStatus.RemoteAdded, live.Find("added-live.txt")!.Status);
        await using (var fs = await h.OpenServerAsync())
        {
            var hashes = await fs.TryHashAsync(["index.html"], CancellationToken.None);
            if (server == "ssh")
                Assert.AreEqual(Hashing.OfBytes(Encoding.UTF8.GetBytes("<h1>shop</h1>")), hashes?["index.html"], "OpenSSH with a shell hashes files itself.");
            if (server == "chroot") Assert.IsNull(hashes, "An SFTP-only account can't run sha256sum - its files are read back.");
        }

        var pulled = await h.PullAsync(live, "Portals/0/site.css");
        Assert.IsTrue(pulled.Success, pulled.Error + Environment.NewLine + h.Reporter.Text);
        Assert.AreEqual("body { color: blue; } /* hotfix */", h.ReadLocal("Portals/0/site.css"));
        Assert.IsNull(h.ReadLocal("added-live.txt"), "Only what was chosen is pulled.");

        // A change of the project, pushed and rolled back.
        h.WriteLocal("index.html", "<h1>shop v2</h1>");
        var next = await h.ScanAsync();
        var push2 = await h.PushAsync(next, "index.html");
        Assert.IsTrue(push2.Success, push2.Error);
        var rollback = await h.RollbackAsync(push2.Value!.Id);
        Assert.IsTrue(rollback.Success, rollback.Error + Environment.NewLine + h.Reporter.Text);
        await using (var fs = await h.OpenServerAsync())
        {
            using var content = new MemoryStream();
            await fs.DownloadAsync("index.html", content, 0, null, CancellationToken.None);
            Assert.AreEqual("<h1>shop</h1>", Encoding.UTF8.GetString(content.ToArray()));
            await fs.DeleteAsync("added-live.txt", CancellationToken.None);
        }
        Assert.AreEqual(SyncStatus.LocalModified, (await h.ScanAsync()).Find("index.html")!.Status);
        await ClearAsync(h);
    }

    [TestMethod]
    [DataRow("ssh")]
    [DataRow("ftps")]
    public async Task A_server_that_shows_another_identity_is_refused(string server)
    {
        RequireDocker();
        var connection = Connection(server) with { TrustedHostKey = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        var check = await new DeployConnections(new DeployHarness().Store, new RemoteFileSystemFactory())
            .TestAsync(connection, new DeploySecrets(Password), CancellationToken.None);
        Assert.IsFalse(check.Ok);
        Assert.IsTrue(check.IdentityChanged, check.Message);
        StringAssert.Contains(check.Message, "posing as it");
    }

    [TestMethod]
    [DataRow("ssh")]
    [DataRow("ftp")]
    public async Task A_wrong_password_is_said_as_such(string server)
    {
        RequireDocker();
        var connection = await TrustedAsync(Connection(server));
        var check = await new DeployConnections(new DeployHarness().Store, new RemoteFileSystemFactory())
            .TestAsync(connection, new DeploySecrets("wrong"), CancellationToken.None);
        Assert.IsFalse(check.Ok);
        StringAssert.Contains(check.Message, "Signing in");
    }

    [TestMethod]
    public async Task The_lock_is_made_exclusively_and_permissions_are_kept()
    {
        RequireDocker();
        var connection = await TrustedAsync(Connection("ssh"));
        using var h = new DeployHarness(connection, new DeploySecrets(Password), new RemoteFileSystemFactory());
        await using var fs = await h.OpenServerAsync();
        Assert.IsTrue(await fs.TryCreateAsync("lock-test", "a"u8.ToArray(), CancellationToken.None));
        Assert.IsFalse(await fs.TryCreateAsync("lock-test", "b"u8.ToArray(), CancellationToken.None), "A second exclusive make is refused.");
        await fs.DeleteAsync("lock-test", CancellationToken.None);

        h.WriteLocal("secret-ish.php", "<?php // v1");
        var scan = await h.ScanAsync();
        Assert.IsTrue((await h.PushAsync(scan, "secret-ish.php")).Success, h.Reporter.Text);
        await fs.SetPermissionsAsync("secret-ish.php", Convert.ToInt32("640", 8), CancellationToken.None);
        h.WriteLocal("secret-ish.php", "<?php // v2, longer");
        Assert.IsTrue((await h.PushAsync(await h.ScanAsync(), "secret-ish.php")).Success, h.Reporter.Text);
        Assert.AreEqual(Convert.ToInt32("640", 8), (await fs.StatAsync("secret-ish.php", CancellationToken.None))!.Permissions,
            "A replaced file keeps the permissions it had.");
        await ClearAsync(h);
    }

    /// <summary>Removes what a test sent, so the next one starts from an empty folder.</summary>
    private static async Task ClearAsync(DeployHarness h)
    {
        await using var fs = await h.OpenServerAsync();
        foreach (var file in await fs.ListAsync(_ => false, _ => { }, _ => { }, CancellationToken.None))
        {
            await fs.DeleteAsync(file.Path, CancellationToken.None);
            await fs.DeleteEmptyFoldersAsync(DeployPath.Folder(file.Path), CancellationToken.None);
        }
    }
}
