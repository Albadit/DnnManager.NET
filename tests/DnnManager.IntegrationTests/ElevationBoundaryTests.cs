using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// DNN Manager runs with administrator rights; what any program of the user (or a site's app pool) can change - the
/// settings, environment variables, a site's folder, a downloaded package - mustn't take those rights elsewhere.
/// </summary>
[TestClass]
public sealed class ElevationBoundaryTests
{
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [TestMethod]
    public void The_projects_folder_is_never_a_system_folder_or_one_in_it()
    {
        Assert.IsNull(SettingRules.ProjectsFolderProblem(@"C:\DNN"));
        Assert.IsNull(SettingRules.ProjectsFolderProblem(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DNN")));
        foreach (var refused in new[]
                 {
                     @"C:\", Path.Combine(ProgramFiles, "SomeService"), Path.Combine(ProgramFiles, "SomeService", "plugins"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DnnManager", "tools"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!, "someone-else", "DNN"),
                     @"\\server\share\DNN", "DNN", ""
                 })
            Assert.IsNotNull(SettingRules.ProjectsFolderProblem(refused), $"'{refused}' was allowed.");
    }

    [TestMethod]
    public void Release_sources_and_names_that_go_into_SQL_are_held_to_their_rules()
    {
        Assert.IsTrue(SettingRules.IsReleaseSource("https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases"));
        Assert.IsTrue(SettingRules.IsReleaseSource("http://localhost:8080/releases"));
        Assert.IsFalse(SettingRules.IsReleaseSource("http://example.com/releases"));
        Assert.IsFalse(SettingRules.IsReleaseSource("file:///C:/releases"));

        Assert.IsTrue(SettingRules.IsHostnameSuffix("dnndev.me"));
        foreach (var bad in new[] { "dnn dev.me", "dnndev.me\nDROP DATABASE x", "dnn'dev.me", "dnn%dev.me", "dnn_dev.me", "" })
            Assert.IsFalse(SettingRules.IsHostnameSuffix(bad), $"'{bad}' was allowed.");

        Assert.IsTrue(SettingRules.IsCollation("Latin1_General_CI_AS"));
        Assert.IsFalse(SettingRules.IsCollation("Latin1_General_CI_AS; DROP DATABASE x"));
    }

    [TestMethod]
    public void Environment_overrides_are_held_to_the_rules_of_the_saved_settings()
    {
        Assert.AreEqual(0, new UserSettings().ToAppOptions().Problems().Count, "The defaults aren't allowed.");

        var overridden = new UserSettings().ToAppOptions();
        overridden.BaseDirectory = Path.Combine(ProgramFiles, "SomeService");
        overridden.Docker.Collation = "Latin1_General_CI_AS; DROP DATABASE x";
        overridden.GitHubReleaseApis = ["http://example.com/releases"];
        Assert.AreEqual(3, overridden.Problems().Count, string.Join(" | ", overridden.Problems()));
    }

    [TestMethod]
    public void The_projects_folder_guard_leaves_a_system_folder_alone()
    {
        var folder = Path.Combine(ProgramFiles, $"dnnmanager-test-{Guid.NewGuid():N}");
        var guard = new ProjectsFolderGuard(Options.Create(new AppOptions()), NullLogger<ProjectsFolderGuard>.Instance);
        Assert.IsFalse(guard.Secure(folder));
        Assert.IsFalse(Directory.Exists(folder), "It was made.");
    }

    [TestMethod]
    public void A_path_under_a_folder_stays_in_it_and_out_of_links()
    {
        using var temp = new TempFolders();
        var root = temp.Make("site");
        Assert.IsNotNull(SafePath.Under(root, @"bin\DotNetNuke.dll"));
        foreach (var escaping in new[] { @"..\outside.dll", @"bin\..\..\outside.dll", @"C:\Windows\x.dll", "file.txt:stream", @"\\server\x" })
            Assert.IsNull(SafePath.Under(root, escaping), $"'{escaping}' was allowed.");

        var elsewhere = temp.Make("elsewhere");
        Junction(Path.Combine(root, "bin"), elsewhere);
        Assert.IsTrue(SafePath.IsLink(Path.Combine(root, "bin")));
        Assert.IsTrue(SafePath.HasLink(root, Path.Combine(root, "bin", "sub")));
        Assert.IsNull(SafePath.Under(root, @"bin\evil.dll"));
        Assert.IsFalse(SafePath.HasLink(root, Path.Combine(root, "Portals")));
    }

    [TestMethod]
    public async Task Restoring_a_backup_doesnt_write_through_a_junction_in_the_site()
    {
        using var temp = new TempFolders();
        var site = temp.Make("site");
        var elsewhere = temp.Make("elsewhere");
        Junction(Path.Combine(site, "bin"), elsewhere);
        var zip = Path.Combine(temp.Make("zips"), "backup.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("web.config").Open())) w.Write("<configuration />");
            using (var w = new StreamWriter(archive.CreateEntry("bin/evil.dll").Open())) w.Write("not DNN's");
        }

        var result = await new ProjectFileCopier().ExtractZipAsync(zip, site, new RecordingReporter(), CancellationToken.None);

        Assert.IsFalse(result.Success, "It extracted through the junction.");
        Assert.IsFalse(File.Exists(Path.Combine(elsewhere, "evil.dll")), "The file landed outside the site.");
    }

    [TestMethod]
    public async Task Clearing_the_cache_doesnt_delete_through_a_junction()
    {
        using var temp = new TempFolders();
        var site = temp.Make("site");
        Directory.CreateDirectory(Path.Combine(site, "bin"));
        File.WriteAllText(Path.Combine(site, "bin", "DotNetNuke.dll"), "");
        Directory.CreateDirectory(Path.Combine(site, "Portals", "_default"));
        var elsewhere = temp.Make("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "not the cache");
        Directory.CreateDirectory(Path.Combine(elsewhere, "keep-folder"));
        Junction(Path.Combine(site, "Portals", "_default", "Cache"), elsewhere);

        var reporter = new RecordingReporter();
        var result = await new ClearSiteCacheUseCase(new UntouchedIis(), new YesPrompt()).ExecuteAsync("site", site, reporter, CancellationToken.None);

        Assert.IsTrue(result.Success, reporter.Text);
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "keep.txt")), "A file the junction points to was deleted.");
        Assert.IsTrue(Directory.Exists(Path.Combine(elsewhere, "keep-folder")), "A folder the junction points to was deleted.");
        StringAssert.Contains(reporter.Text, "link or junction");
    }

    [TestMethod]
    public async Task A_DNN_package_that_isnt_the_file_GitHub_lists_isnt_used()
    {
        using var temp = new TempFolders();
        const string url = "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.0.0/DNN_Platform_10.0.0_Install.zip";
        var zip = Path.Combine(temp.Make("zips"), "package.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("web.config").Open())) w.Write("<configuration />");
        var installer = new DnnPackageInstaller(new HttpClient(new Serving(url, zip)), Options.Create(new AppOptions()),
            new AppDataPaths(temp.Make("data")), NullLogger<DnnPackageInstaller>.Instance);
        var site = temp.Make("site");

        var changed = await installer.DownloadAndExtractAsync(new DnnRelease("10.0.0", "v10.0.0", url, Sha256: new string('0', 64)), site,
            new RecordingReporter(), CancellationToken.None);
        Assert.IsFalse(changed.Success, "A package with another SHA-256 was used.");
        StringAssert.Contains(changed.Error, "SHA-256");
        Assert.IsFalse(File.Exists(Path.Combine(site, "web.config")), "Its files went in.");

        var overHttp = await installer.DownloadAndExtractAsync(new DnnRelease("10.0.0", "v10.0.0", url.Replace("https://", "http://")), site,
            new RecordingReporter(), CancellationToken.None);
        Assert.IsFalse(overHttp.Success, "A package was downloaded over http.");

        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip)));
        var matching = await installer.DownloadAndExtractAsync(new DnnRelease("10.0.0", "v10.0.0", url, Sha256: sha), site,
            new RecordingReporter(), CancellationToken.None);
        Assert.IsTrue(matching.Success, matching.Error);
        Assert.IsTrue(File.Exists(Path.Combine(site, "web.config")));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.IsTrue(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"Could not make the junction {link}.");
    }

    [TestMethod]
    public void IIS_feature_names_that_go_to_PowerShell_are_feature_names_only()
    {
        foreach (var good in new[] { "IIS-WebServerRole", "NetFx4Extended-ASPNET45", "WCF-HTTP-Activation45", "IIS-ASPNET45" })
            Assert.IsTrue(SettingRules.IsIisFeatureName(good), $"'{good}' was refused.");
        // PowerShell takes ‘ ’ ‚ ‛ for ' as well - a name with one would close the string it was put in.
        foreach (var bad in new[] { "IIS\u2019; calc; \u2018", "IIS'; calc; '", "IIS-A;IIS-B", "IIS $x", "IIS`n", "", new string('a', 129) })
            Assert.IsFalse(SettingRules.IsIisFeatureName(bad), $"'{bad}' was allowed.");

        var settings = new UserSettings();
        settings.Iis.RequiredFeatures.Add(new IisFeatureSetting { Name = "IIS\u2019; calc; \u2018", Label = "x" });
        Assert.IsTrue(settings.Validate().Any(p => p.Key == "iis.requiredFeatures"));
        var options = new UserSettings().ToAppOptions();
        options.RequiredIisFeatures = [new IisFeatureSetting { Name = "IIS'; calc; '", Label = "x" }];
        Assert.IsTrue(options.Problems().Any(p => p.StartsWith("RequiredIisFeatures", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void A_program_started_as_Administrator_gets_none_of_the_users_code_loading_environment()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CORECLR_ENABLE_PROFILING"] = "1", ["CORECLR_PROFILER_PATH"] = @"C:\x.dll", ["COR_PROFILER"] = "{x}",
            ["DOTNET_DiagnosticPorts"] = "x", ["DOTNET_EnableEventPipe"] = "1", ["COMPlus_EventPipeOutputPath"] = @"C:\Windows\x",
            ["NUGET_PACKAGES"] = profile, ["DOCKER_CONTEXT"] = "x", ["DOCKER_HOST"] = "tcp://x", ["SQLCMDINI"] = "x",
            ["PSModulePath"] = Path.Combine(profile, "Documents", "WindowsPowerShell", "Modules"),
            ["SystemRoot"] = @"C:\NOT-WINDOWS", ["ProgramData"] = profile, ["PATH"] = profile, ["KEEP_ME"] = "1"
        };
        ChildEnvironment.Harden(environment);

        foreach (var gone in new[] { "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "COR_PROFILER", "DOTNET_DiagnosticPorts",
                                     "DOTNET_EnableEventPipe", "COMPlus_EventPipeOutputPath", "NUGET_PACKAGES", "DOCKER_CONTEXT", "DOCKER_HOST", "SQLCMDINI" })
            Assert.IsFalse(environment.ContainsKey(gone), $"{gone} was kept.");
        Assert.AreEqual("1", environment["KEEP_ME"]);
        Assert.AreEqual("0", environment["DOTNET_EnableDiagnostics"]);
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.Windows), environment["SystemRoot"]);
        Assert.AreEqual(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), environment["ProgramData"]);
        // Windows PowerShell finds the modules of Windows and Program Files only - none in the user's Documents.
        Assert.IsFalse(environment["PSModulePath"]!.Contains(profile, StringComparison.OrdinalIgnoreCase), environment["PSModulePath"]);
        // The computer's PATH, with nothing of the user's environment filled into it.
        Assert.IsFalse(environment["PATH"]!.Contains('%'), environment["PATH"]);
        Assert.IsFalse(environment["PATH"]!.Contains("NOT-WINDOWS", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(environment["PATH"]!.Contains(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase), environment["PATH"]);
        // docker: a configuration of DNN Manager's own, not the user's .docker (its context, plugins, credential helpers).
        Assert.IsFalse(string.Equals(Path.TrimEndingDirectorySeparator(environment["DOCKER_CONFIG"]!), Path.Combine(profile, ".docker"),
            StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(2, ChildEnvironment.CodeLoadingVariables(new System.Collections.Hashtable
        {
            ["CORECLR_PROFILER_PATH_64"] = "x", ["DOTNET_EventPipeOutputPath"] = "x", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["PATH"] = "x"
        }).Count);
    }

    [TestMethod]
    public void A_server_a_web_config_names_is_this_PC_only_when_it_really_is()
    {
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine("."));
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine(Environment.MachineName));
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine(@"np:\\.\pipe\sql\query"));
        // A pipe's path alone is opened as a pipe - on the server after the backslashes, not on this PC.
        foreach (var elsewhere in new[] { @"\\evil.example\pipe\sql\query", @"np:\\evil.example\pipe\sql\query", @"tcp:\\evil.example\pipe\x",
                                          $"{Environment.MachineName}.evil.example", "db.example.com" })
            Assert.IsFalse(SqlServerAddress.IsOnThisMachine(elsewhere), $"'{elsewhere}' was taken for this PC.");
        Assert.AreEqual("evil.example", SqlServerAddress.Parse(@"\\evil.example\pipe\sql\query").Host);
    }

    [TestMethod]
    public void IIS_gets_no_more_than_it_needs_on_a_site_an_older_version_made()
    {
        using var temp = new TempFolders();
        var site = new DirectoryInfo(temp.Make("site"));
        var iisUsers = new System.Security.Principal.SecurityIdentifier("S-1-5-32-568");
        var pool = new System.Security.Principal.SecurityIdentifier("S-1-5-82-1-2-3-4-5");
        var security = site.GetAccessControl();
        foreach (var sid in new[] { iisUsers, pool })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        site.SetAccessControl(security);

        Assert.IsTrue(ProjectsFolderGuard.TightenSite(site));
        var rules = site.GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        var mask = ~System.Security.AccessControl.FileSystemRights.Synchronize;
        Assert.AreEqual(System.Security.AccessControl.FileSystemRights.ReadAndExecute & mask,
            rules.Single(r => r.IdentityReference.Equals(iisUsers)).FileSystemRights & mask);
        Assert.AreEqual(System.Security.AccessControl.FileSystemRights.Modify & mask,
            rules.Single(r => r.IdentityReference.Equals(pool)).FileSystemRights & mask);
        Assert.IsFalse(ProjectsFolderGuard.TightenSite(site), "Lowered twice.");

        // A projects folder an administrator made: everyone's groups out, nobody added.
        var open = new System.Security.AccessControl.DirectorySecurity();
        var authenticated = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
        foreach (var sid in new[] { authenticated, iisUsers })
            open.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow));
        var closed = ProjectsFolderGuard.WithoutEveryone(open).GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference).ToList();
        CollectionAssert.AreEqual(new[] { iisUsers }, closed);
    }

    private sealed class YesPrompt : IUserPrompt
    {
        public Task<bool> ConfirmAsync(string question, string yes, string no, bool defaultYes = false, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class Serving(string url, string file) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.AbsoluteUri == url
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Folders in %TEMP%, removed with what is in them - junctions unlinked, never followed.</summary>
    private sealed class TempFolders : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"dnnmanager-boundary-{Guid.NewGuid():N}")).FullName;

        public string Make(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
