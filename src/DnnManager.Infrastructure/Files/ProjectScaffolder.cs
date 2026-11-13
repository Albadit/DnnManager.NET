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
    // Leaves out build output, NuGet/node packages, DNN runtime data (caches, search index, logs),
    // database files and portal uploads. Everything DNN needs to run stays tracked: bin/ (with
    // bin/x64 and bin/x86), Portals/_default, Install/*.resources and portal-level themes.
    private const string GitignoreTemplate =
"""
# ---------------------------------------------------------------------
# .gitignore for a DNN (DotNetNuke) site, written by DNN Manager.
# Tracks the site's code, themes and configuration; leaves out build
# output, runtime data (caches, search index, logs), database files and
# portal uploads. A site keeps its own .gitignore once it has one.
# ---------------------------------------------------------------------

# ===== IDEs =====
.vs/
.vscode/
.idea/
*.suo
*.user
*.userosscache
*.userprefs
*.sln.docstates
*.DotSettings.user
_ReSharper*/
*.[Rr]e[Ss]harper

# ===== Build output of module/theme source projects =====
# The site's own bin/ stays tracked: it holds the assemblies DNN runs on,
# native libraries in bin/x64 and bin/x86 included.
obj/
**/[Bb]in/[Dd]ebug/
**/[Bb]in/[Rr]elease/
/build/
/bld/
/artifacts/
packages/
*.nupkg
*.snupkg
project.lock.json
project.fragment.lock.json
*.tmp
*.tmp_proj
*.cache

# ===== Node / front-end tooling =====
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

# ----- Logs (log4net writes Portals/_default/Logs/*.log.resources) -----
*.log
*.log.resources
*.svclog
[Ll]og/
[Ll]ogs/

# ----- Runtime data in App_Data -----
App_Data/Search/
App_Data/_imagecache/
App_Data/_ipcount/
App_Data/ClientDependency/
App_Data/ExportImport/
App_Data/ExtensionPackages/
App_Data/FipsCompilanceAssemblies/
App_Data/Upgrade/
App_Data/Backup_*/

# ----- File-based cache -----
Portals/_default/Cache/

# ----- Database files and backups (they hold the site's data) -----
*.mdf
*.ldf
*.bak
*.bacpac
*.orig

# ----- Install leftovers -----
# Install/*.resources and Install/<type>/*.resources are DNN's install
# template and bundled packages: they stay tracked.
Install/Temp/

# ----- Config backups made by upgrades -----
Config/Backup_*/
web.config.bak
web_*.config
*.config.bak

# ----- Portal folders: uploads stay out, portal themes stay in -----
# Portals/_default holds core themes and templates and stays tracked.
Portals/[0-9]*/*
!Portals/[0-9]*/Skins/
!Portals/[0-9]*/Containers/
!Portals/[0-9]*/portal.css

# ----- Visual Studio web publishing -----
PublishProfiles/
*.pubxml
*.pubxml.user
*.publishproj
*.azurePubxml

# ----- Local overrides -----
*.local
*.local.config
""";
}
