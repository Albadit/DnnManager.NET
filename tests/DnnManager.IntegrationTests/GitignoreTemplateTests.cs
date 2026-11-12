using System.Diagnostics;
using DnnManager.Infrastructure.Files;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The .gitignore DNN Manager writes into a site, put to git itself: what a DNN site runs from stays trackable - the
/// files generic .gitignores drop (js/Debug, bin/x64, dist/, Install packages, 2sxc apps and markers, portal themes) -
/// and what is made at run time, uploaded, logged, backed up or secret is ignored. The paths are ones found on real
/// sites and in DNN Platform 10.4.1's package (see .docs/dnn-gitignore-audit.md). Case-sensitive, as git on a CI runner.
/// </summary>
[TestClass]
public sealed class GitignoreTemplateTests
{
    private static readonly string[] Tracked =
    [
        "bin/DotNetNuke.dll", "bin/x64/libwebp.dll", "bin/x86/libwebp.dll", "bin/DnnSharp.lic",
        "js/Debug/dnn.js", "js/fc/packages/core/index.global.min.js",
        "DesktopModules/ToSic.Sxc/dist/edit.js", "DesktopModules/DNNGo_ThemePluginPro/Resource/plugins/build/x.js",
        "DesktopModules/ToSic.Sxc/Upgrade/Log/08.00.00.resources", "DesktopModules/MVC/Web.config",
        "Install/Web.config", "Install/AuthSystem/DNN_Facebook_10.4.1_Install.resources", "Install/DotNetNuke.install.config.resources",
        "Licenses/jQuery (MIT).txt.resources", "Resources/AppOffline/App_Offline.htm.resources",
        "Portals/_default/Default Website.template.resources", "Portals/_default/Logs/LogConfig/LogConfigTemplate.xml.resources",
        "Portals/_default/Skins/_default/No Skin.ascx", "Portals/_default/Cache/PlaceHolder.txt",
        "App_Data/FipsCompilanceAssemblies/Lucene.Net.dll", "App_Data/Upgrade/upgrade.json", "App_Data/ExportImport/PlaceHolder.txt",
        "Portals/0/2sxc/Blog6/_List.cshtml", "Portals/0/Skins/Site/home.ascx", "Portals/0-System/Skins/Site/home.ascx",
        "Portals/0/Containers/Site/box.ascx", "Portals/0/portal.css", "Portals/0/GoogleAnalytics4.config", "Portals/0/_footer.ascx",
        "_backup.filter", ".vscode/settings.json", "Portals/0/2sxc/Content/.vscode/tasks.json", "DotNetNuke.config", "SiteUrls.config"
    ];

    private static readonly string[] Ignored =
    [
        "web.config", "web.config.bak-ssl", "web.config_", "web_.config", "web - kopie.config", "Config/Backup_202101181609/web_old.config",
        "App_Data/Database.mdf", "App_Data/Search/_0.cfs", "App_Data/ExtensionPackages/JavaScript_Library_jQuery_3.7.1.resources",
        "App_Data/ExportImport/2026-06-29_14-03-17/export.zip", "App_Data/ClientDependency/map.xml", "App_Data/2sxc.bin/App-00002-AppCode.dll",
        "App_Data/imageflow_hybrid_cache/11/00/a.jpg", "App_Data/BulkInstall/Sessions/s.zip", "Portals/_default/Cache/MyTokens/x.cache",
        "Portals/0/adam/Content/photo.jpg", "Portals/0/NBStore/images/p.jpg", "Portals/0/Users/032/32/32/avatar.png", "Portals/0/logo.png",
        "Portals/0-System/Sitemap/sitemap.xml", "Portals/_default/Logs/2026.11.05.log.resources",
        "DesktopModules/ToSic.Sxc/Upgrade/Log/2026-01-01 10-00-00-123.log.resources",
        "Providers/DataProviders/SqlDataProvider/09.10.00.log.resources", "Install/Temp/ierx5rqe/bin/x.dll", "installBlocker.lock",
        "01_backup/site_20260928/site.bacpac", "backups/clone.bacpac", "DesktopModules/X/obj/Debug/net472/x.cache",
        "DesktopModules/X/node_modules/a/index.js", "Portals/_default/Skins/Site/node_modules/.package-lock.json",
        "workspace/docker/.env", "workspace/docker/.env.production", ".vs/x/v17/.suo", "imagecache/0088/a.jpg", "certs/site.pfx",
        "DesktopModules/X/a.js.bak", "Portals/_default/ContentBuilder/Thumbs.db"
    ];

    [TestMethod]
    public void A_DNN_site_keeps_what_it_runs_from_and_ignores_what_it_makes()
    {
        var site = TestEnvironment.NewRunDirectory();
        try
        {
            Assert.IsTrue(new ProjectScaffolder(NullLogger<ProjectScaffolder>.Instance).EnsureGitignore(site).Success);
            foreach (var path in Tracked.Concat(Ignored))
            {
                var file = Path.Combine(site, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "");
            }
            if (Git(site, "init -q") is null) Assert.Inconclusive("git isn't on the PATH.");
            var ignored = (Git(site, "-c core.ignorecase=false check-ignore --stdin", string.Join('\n', Tracked.Concat(Ignored))) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

            var dropped = Tracked.Where(ignored.Contains).ToList();
            Assert.AreEqual(0, dropped.Count, "Ignored, though a site needs them: " + string.Join(", ", dropped));
            var kept = Ignored.Where(p => !ignored.Contains(p)).ToList();
            Assert.AreEqual(0, kept.Count, "Not ignored: " + string.Join(", ", kept));
        }
        finally
        {
            TestEnvironment.DeleteDirectory(site);
        }
    }

    [TestMethod]
    public void A_site_with_its_own_gitignore_keeps_it()
    {
        var site = TestEnvironment.NewRunDirectory();
        try
        {
            File.WriteAllText(Path.Combine(site, ".gitignore"), "mine\n");
            Assert.IsTrue(new ProjectScaffolder(NullLogger<ProjectScaffolder>.Instance).EnsureGitignore(site).Success);
            Assert.AreEqual("mine\n", File.ReadAllText(Path.Combine(site, ".gitignore")));
        }
        finally
        {
            TestEnvironment.DeleteDirectory(site);
        }
    }

    // git's output, or null when git can't be started.
    private static string? Git(string directory, string arguments, string? input = null)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{directory}\" {arguments}")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            })!;
            if (input is not null) git.StandardInput.Write(input + "\n");
            git.StandardInput.Close();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
