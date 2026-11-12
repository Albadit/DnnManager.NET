using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Files;

public sealed class ProjectScaffolder(ILogger<ProjectScaffolder> log) : IProjectScaffolder
{
    private readonly ILogger<ProjectScaffolder> _log = log;

    public Result EnsureGitignore(string projectDirectory)
    {
        try
        {
            Directory.CreateDirectory(projectDirectory);
            var path = Path.Combine(projectDirectory, ".gitignore");

            // Never clobber a site that already manages its own .gitignore (e.g. a cloned project).
            if (File.Exists(path))
                return Result.Ok();

            File.WriteAllText(path, GitignoreTemplate);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            // A missing .gitignore must not abort setup/clone - report and move on.
            _log.LogWarning(ex, "Could not write .gitignore to {Dir}", projectDirectory);
            return Result.Fail(ex.Message);
        }
    }

    // The .gitignore written into every managed DNN *site* directory (not this tool's repo). Built from a scan of
    // real sites and DNN Platform 10.4.1's own files, every rule checked with git - see .docs/dnn-gitignore-audit.md.
    // Keeps runtime data, uploads, logs, backups and secrets out; keeps everything a site runs from, which generic
    // .gitignores drop (bin/, js/Debug, bin/x64, dist/, Install packages, 2sxc apps, portal themes).
    private const string GitignoreTemplate =
"""
# ---------------------------------------------------------------------
# .gitignore for DotNetNuke (DNN) site - written by DNN Manager
# ---------------------------------------------------------------------
# (Checked against DNN Platform 10.4.1: of its own files only web.config
#  and the empty App_Data/Database.mdf are ignored)
# (Files git already tracks stay tracked - `git rm --cached <path>`)

# ===== Visual Studio / Build =====
# (obj/ only - bin/, js/Debug/, bin/x64/, bin/x86/ and build/ are files
#  the site runs from)
*.suo
*.user
*.userosscache
*.userprefs
*.sln.docstates
*.swp
*.tmp_proj
*_wpftmp.csproj
*.vspscc
*.vssscc
*.scc
*.psess
*.vsp
*.vspx
*.sap
*.svclog

.vs/
[Oo]bj/

# ===== VS Code =====
# (Shared settings and tasks are kept - 2sxc apps ship them)
.vscode/*
!.vscode/settings.json
!.vscode/tasks.json
!.vscode/launch.json
!.vscode/extensions.json

# ===== ReSharper / Rider =====
_ReSharper*/
*.DotSettings.user
.idea/

# ===== NuGet / Packages =====
# (Root packages/ only - js/**/packages/ is served by the site)
/packages/
*.nupkg
*.snupkg
project.lock.json
project.fragment.lock.json

# ===== Node / Front-end build =====
# (dist/ is kept - modules serve their built files from it)
node_modules/
jspm_packages/
.sass-cache/
npm-debug.log*
yarn-debug.log*
yarn-error.log*

# ===== Python =====
__pycache__/
*.py[cod]

# ===== Test output / coverage =====
[Tt]est[Rr]esult*/
*.trx
*.coverage
*.coveragexml
coverage/
.nyc_output/

# ===== OS =====
Thumbs.db
ehthumbs.db
ehthumbs_vista.db
[Dd]esktop.ini
$RECYCLE.BIN/
.DS_Store
._*

# ---------------------------------------------------------------------
# DNN specific
# ---------------------------------------------------------------------

# ----- DNN App_Data (runtime data, caches, search index) -----
App_Data/*.mdf
App_Data/*.ldf
App_Data/Search/
App_Data/_imagecache/
App_Data/ClientDependency/
App_Data/ExtensionPackages/
App_Data/ExportImport/*
!App_Data/ExportImport/PlaceHolder.txt
App_Data/BulkInstall/Sessions/
# (2sxc compiled app code and Imageflow resized images)
App_Data/2sxc.bin/
App_Data/imageflow_hybrid_cache/

# ----- Cache / generated -----
Portals/_default/Cache/*
!Portals/_default/Cache/PlaceHolder.txt
!Portals/_default/Cache/ReadMe.txt
/imagecache/
.temp_cache/
*.tmp

# ----- Portals user uploads (Files, ADAM, stores - kept by backups) -----
Portals/[0-9]*/*
Portals/_default/Users/
# (Keep each portal's code and settings tracked)
!Portals/[0-9]*/2sxc/
!Portals/[0-9]*/Skins/
!Portals/[0-9]*/Containers/
!Portals/[0-9]*/Templates/
!Portals/[0-9]*/portal.css
!Portals/[0-9]*/*.config
!Portals/[0-9]*/Dnn.CK*.xml
!Portals/[0-9]*/*.ascx
# (DNN 9.2+ keeps portal themes in the system folder, Portals/0-System)
!Portals/[0-9]*-System/*
Portals/[0-9]*-System/Sitemap/

# ----- DNN runtime logs -----
# (Other *.resources files are DNN packages and templates - kept, as are
#  2sxc's Upgrade/Log/NN.NN.NN.resources markers, which 2sxc reads)
*.log
*.log.resources
Portals/_default/Logs/*
!Portals/_default/Logs/LogConfig/

# ----- Install / upgrade leftovers -----
Install/Temp/
installBlocker.lock

# ----- Config backups created by DNN upgrades -----
Config/Backup_*/

# ----- Databases / backups -----
*.mdf
*.ldf
*.bacpac
*.bak
*.bak-*
*.orig
/*[Bb]ackup*/
_backup_*/

# ----- Visual Studio Web publishing -----
*.pubxml.user
*.publishsettings

# ----- Secrets / environment config -----
# (web.config holds the connection string and machine key - delete this
#  line to track it, and keep no live passwords in it)
/web.config
# (Copies: web_.config, "web - kopie.config", web.config.bak-ssl, web.config_)
/web?*.config
/web.config?*
*.config.bak
*.local.config
.env
.env.*
!.env.example

# ----- Certificates / private keys -----
# (Module licence files *.lic are kept - modules need them to run)
*.pfx
*.p12
*.pem
*.key
*.ppk
id_rsa*
id_ed25519*
id_ecdsa*
*.kdbx
""";
}
