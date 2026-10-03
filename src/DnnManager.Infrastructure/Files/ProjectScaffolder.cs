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

    // DNN-tuned .gitignore written into every managed DNN *site* directory (not this tool's repo).
    // Keeps Visual Studio/build output, NuGet/node packages, DNN runtime data (App_Data, caches,
    // search index, logs) and portal uploads out of source control.
    private const string GitignoreTemplate =
"""
# ---------------------------------------------------------------------
# .gitignore for DotNetNuke (DNN) site
# ---------------------------------------------------------------------

# ===== Visual Studio / Build =====
*.suo
*.user
*.userosscache
*.sln.docstates
*.userprefs
*.pidb
*.svclog
*.scc
*.cache
*.tmp
*.tmp_proj
*.log
*.vspscc
*.vssscc
*.psess
*.vsp
*.vspx
*.sap

.vs/
.vscode/
obj/
bin/obj/
[Dd]ebug/
[Rr]elease/
x64/
x86/
build/
bld/
[Ll]og/
[Ll]ogs/

# ===== ReSharper / Rider =====
_ReSharper*/
*.[Rr]e[Ss]harper
*.DotSettings.user
.idea/

# ===== NuGet / Packages =====
packages/
*.nupkg
*.snupkg
project.lock.json
project.fragment.lock.json
artifacts/

# ===== Node / Front-end build =====
node_modules/
npm-debug.log*
yarn-debug.log*
yarn-error.log*

# ===== OS =====
Thumbs.db
ehthumbs.db
Desktop.ini
$RECYCLE.BIN/
.DS_Store

# ---------------------------------------------------------------------
# DNN specific
# ---------------------------------------------------------------------

# ----- Secrets / environment config -----
web.config.bak
web_*.config
*.config.bak
DotNetNuke.config.bak

# ----- DNN runtime logs -----
Portals/_default/Logs/
**/Logs/*.log
**/Logs/*.resources
*.log.resources

# ----- DNN App_Data (runtime data, caches, search index, uploads) -----
App_Data/Database.mdf
App_Data/Database_log.ldf
App_Data/*.mdf
App_Data/*.ldf
App_Data/Search/
App_Data/_imagecache/
App_Data/_ipcount/
App_Data/ClientDependency/
App_Data/ExportImport/
App_Data/ExtensionPackages/
App_Data/FipsCompilanceAssemblies/
App_Data/Upgrade/
App_Data/Backup_*/
App_Data/Tabs.xml.resources
App_Data/PageIndex*

# ----- Cache / generated -----
**/_imagecache/
**/ClientDependency/
**/cache/
**/Cache/

# ----- Install logs / leftovers -----
Install/*.log.resources
Install/InstallWizard.log
Install/Temp/

# ----- Config backups created by DNN upgrades -----
Config/Backup_*/
**/*.config.resources

# ----- Portals user uploads (usually excluded from source control) -----
Portals/[0-9]*/
Portals/_default/Users/
Portals/_default/Cache/
Portals/_default/Skins/_default/
# (Keep custom skins/containers tracked manually if needed)

# ----- DNN compiled providers / temp -----
App_Data/Modules/
App_Code/Generated/

# ----- Misc DNN -----
*.dnn.resources
*.resources
*.bak
*.orig

# ----- Visual Studio Web publishing -----
PublishProfiles/
*.pubxml
*.pubxml.user
*.publishproj
*.azurePubxml

# ----- Local override -----
*.local
*.local.config
""";
}
