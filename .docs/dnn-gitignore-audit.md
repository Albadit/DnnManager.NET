# DNN site .gitignore - audit

DNN Manager writes a `.gitignore` into every DNN site it sets up, clones or
imports ([`ProjectScaffolder`](../src/DnnManager.Infrastructure/Files/ProjectScaffolder.cs),
only when the site has none). This audit rebuilt that template from a scan of
real sites and of DNN's own package, and checked every rule with git. It is
about the **sites'** `.gitignore`, not this repository's.

Done 2026-11-11. **Verified** means checked against files on disk or with git;
**assumed** means it rests on DNN's documented behaviour or on a file's name,
without reading the file.

## Summary

- The old template ignored **5,599 files (136 MB) the sites need**, among them
  138 of DNN 10.4.1's own files: DNN's debug scripts (`js/Debug`), native DLLs
  (`bin/x64`, `bin/x86`), served scripts (`js/fc/packages`), every 2sxc app's
  code and the portal themes (`Portals/0/2sxc`, `Portals/0-System/Skins`),
  DNN's default skin, its site template, licence and log-config files, the FIPS
  Lucene DLL, and 2sxc's upgrade markers.
- It did **not** ignore the site's `web.config` (database connection and
  machine key) or most of its copies, database backups in the site, or `.env`
  files.
- The new template ignores what DNN, IIS and the tools make while they run,
  uploads, logs, backups and secrets - and keeps everything a site runs from.
  Of DNN 10.4.1's files it ignores only `web.config` and the empty
  `App_Data/Database.mdf`, both on purpose.
- None of the scanned sites is a git repository yet, so **no secret is in any
  git history** (verified: no `.git` folder in any site).
- Regression test: [GitignoreTemplateTests](../tests/DnnManager.IntegrationTests/GitignoreTemplateTests.cs)
  puts the template to git with 71 paths found on the sites (34 to keep, 37
  to ignore).

## Repository overview

| | |
|---|---|
| Sites scanned (read only) | 8, under `C:\DNN` - 87,747 files, 7.4 GB |
| DNN versions on them (verified, `bin\DotNetNuke.dll`) | 9.8.1 (2), 9.13.2, 10.1.2, 10.3.2, 10.3.3, 10.4.0 (2) |
| Latest stable DNN Platform (verified, GitHub releases) | **10.4.1**, released 2026-10-28 |
| Reference packages | `DNN_Platform_10.4.1_Install.zip` (3,138 entries), and 9.8.1, 9.13.2 and 10.1.2 - the versions the sites run - listed in a scratch folder, never installed |
| Older snapshots | DNN Manager's own backups of dzp (2026-10-26) and sparki (2026-11-05), listed from their zips - modules since removed from the live sites |
| Other DNN sites | none: DNN Manager's database knows only `C:\DNN`, and no other `bin\DotNetNuke.dll` was found under `C:\inetpub`, the user's folders or other drives |
| Old template on the sites | 7 of 8 carry it unchanged (2,455 characters); `vankortenhof` has no `.gitignore` |
| Nested `.gitignore` files | 30+ shipped by vendors: every 2sxc app, DnnSharp, `js/slick` |

Main extensions in use: 2sxc (`DesktopModules/ToSic.Sxc`, apps in
`Portals/0/2sxc`), DnnSharp (Action Form, SearchBoost, MyTokens), NBright /
NBStore, DNNGo modules and themes, Plant an App, Imageflow, DNN's Persona Bar
and Bulk Install.

## How it was checked

1. Every site's file names and sizes were listed (no contents read).
2. Each site and the official package were mirrored as **empty files** in a
   scratch folder - names only, so no secret was copied - with the vendors'
   nested `.gitignore` files copied as they are, since git applies them too.
3. The old and the new template were applied to every mirror; `git ls-files
   -o -i --exclude-standard` gave what each ignores, `git check-ignore -v` the
   rule behind every ignored file (all 22,000+ attributed).
4. The two results were compared file by file, and the new one run again
   case-sensitive (`core.ignorecase=false`, as git on Linux or a CI runner):
   the same files.

## Problems with the old template (verified)

| Rule | What it did |
|---|---|
| `*.resources` | Ignored DNN's packages and data: `Install/**/*.resources` (the extensions DNN installs), `Licenses/*.resources`, `Default Website.template.resources`, `Logs/LogConfig/*.xml.resources`, Persona Bar data - 59 of DNN 10.4.1's files. DNN names logs **`*.log.resources`**; only those are logs. |
| `[Dd]ebug/`, `[Rr]elease/` | Ignored DNN's `js/Debug/` (30 core scripts, on every site). |
| `x64/`, `x86/` | Ignored `bin/x64/libwebp.dll` and `bin/x86/libwebp.dll` - native DLLs the site loads. |
| `packages/` | Ignored 448 served scripts (`js/fc/packages`, a module's `_external/packages`). |
| `build/` | Ignored a DNNGo plugin's scripts (`Resource/plugins/*/build`). |
| `Portals/[0-9]*/` | Ignored the whole portal: uploads (right) but also **every 2sxc app's code** (3,446 files) and, through `0-System`, the portal's **themes** (DNN 9.2+ keeps them in `Portals/0-System/Skins`). |
| `Portals/_default/Skins/_default/` | Ignored DNN's own default skin (40 of 10.4.1's files). |
| `Portals/_default/Logs/` | Also took DNN's `LogConfig/` templates. |
| `[Ll]og/` | Ignored 2sxc's upgrade **markers** (`Upgrade/Log/08.00.00.resources`, `lock.resources`) with its logs - 2sxc reads the markers to know which upgrades ran. |
| `App_Data/FipsCompilanceAssemblies/`, `App_Data/Upgrade/` | DNN's own files (FIPS Lucene DLL, `upgrade.json`). |
| `web_*.config`, `web.config.bak` | Missed the site's `web.config` itself and its real copies on the sites: `web - kopie.config`, `web.config.bak-ssl`, `web.config.bak-rewrite`, `web.config_`. |
| Missing | Database backups in the site (`01_backup/*.bacpac`, a 182 MB site zip, `backups/*.bacpac`), `.env` files, Imageflow's and a root image cache, 2sxc's compiled code, Bulk Install sessions, Python caches. |
| Unused | `App_Code/Generated/`, `App_Data/Modules/`, `App_Data/_ipcount/`, `App_Data/PageIndex*` and others matched nothing on any site or in DNN 10.4.1 - dropped. |

## The new template

Sections, in order: OS files; IDEs and editors (VS Code's shared
`settings.json`, `tasks.json`, `launch.json`, `extensions.json` kept - 2sxc
apps ship them); .NET build output (`obj/` only); Visual Studio temporary
files; DNN runtime data; portal uploads; install and upgrade leftovers; logs;
temporary files and caches; NuGet; frontend dependencies; test output;
databases and backups; publishing; environment configuration; secrets. The
exceptions sit next to the rules they belong to, and the header lists what is
kept on purpose.

The rule that needs a decision per site is **portal uploads**:
`Portals/[0-9]*/*` ignores a portal's files (Files, ADAM, a store's products,
user folders - 2.7 GB on these sites) but keeps its code and settings: `2sxc/`,
`Skins/`, `Containers/`, `Templates/`, `portal.css`, `*.config`, `Dnn.CK*.xml`,
`*.ascx`, and the `-System` folder (bar its generated `Sitemap/`). A site whose
uploaded images are part of its design (a logo in `Portals/0/`) needs those
added back, or restored from a backup with the database.

## File classification

| Class | What |
|---|---|
| **MUST TRACK** | `bin/` (DNN and module assemblies, `x64`/`x86` native DLLs, licence files), `DesktopModules/` (with `dist/`, `build/`, nested `Web.config`), `Providers/` (SQL scripts), `Install/` packages, `admin/`, `controls/`, `Resources/`, `js/` (with `js/Debug`), `Licenses/`, `App_GlobalResources/`, `App_Browsers/`, root `.aspx`/`.asax`, `Portals/_default/` (skins, containers, templates, `LogConfig`), `Portals/<n>/2sxc`, portal themes (`Portals/<n>/Skins`, `Portals/<n>-System/Skins`), 2sxc upgrade markers |
| **SHOULD TRACK** | `Config/*.config`, root `DotNetNuke.config`, `SiteUrls.config`, `SiteAnalytics.config`, `Serilog.config`, `DotNetNuke.log4net.config`, `_backup.filter` (DNN Manager's backup exclusions), `.vscode/settings.json`/`tasks.json`, `*.zip.manifest`, project folders (`docs/`, `workspace/` minus its secrets) |
| **SAFE TO IGNORE** | `App_Data/{Search,ClientDependency,_imagecache,ExtensionPackages,2sxc.bin,imageflow_hybrid_cache,BulkInstall/Sessions}`, `App_Data/ExportImport/*`, `Portals/_default/{Cache,Logs}/*`, `*.log`, `*.log.resources`, `Install/Temp/`, `Config/Backup_*/`, `installBlocker.lock`, `obj/`, `.vs/`, `node_modules/`, `/imagecache/`, `__pycache__/`, `*.bak` |
| **CONDITIONAL** | Portal uploads (`Portals/<n>/*` - ignored by default), `App_Data/ExtensionPackages/` (1.7 GB of installable packages; ignored - a clone's Extensions page then lists none as available), `*.pdb` and `*.map` (kept: served or harmless), vendors' large binaries (`DesktopModules/DnnSharp/**/*.exe`, 42 MB - kept) |
| **SENSITIVE - REVIEW REQUIRED** | Root `web.config` and its copies (ignored); `App_Data/*.lic`, `bin/DnnSharp.lic`, 2sxc's `default.license.json*` (licence keys - kept, modules need them; keep the repository private); `workspace/**/.env*` (ignored); `01_backup/`, `backups/` (database exports - ignored) |

## Security findings

All from file names; no file content was read or printed.

1. **The site's `web.config` was not ignored.** DNN keeps the database
   connection (`SiteSqlServer`) and the machine key in it (assumed from DNN's
   format). The new template ignores `/web.config`; DNN Manager writes it when
   it sets a site up, clones or imports one, and its deployments already treat
   it as per-environment. A team that tracks it should first move the password
   out (Windows authentication, or a transform kept outside git).
2. **Copies of `web.config` with the same secrets** on 6 sites:
   `Config/Backup_*/web_.config` and `web_old.config` (DNN's own upgrade
   backups), `web - kopie.config`, `web.config.bak-ssl`,
   `web.config.bak-rewrite`, `web.config_`, `01_backup/web.config`. All
   ignored now. Worth deleting the stray ones from the sites.
3. **Database backups inside sites**: `01_backup/<site>.bacpac` and a 182 MB
   site zip (ceesboer), `backups/clone_*.bacpac` (dzp), Export/Import packages
   in `App_Data/ExportImport` (81 MB). All ignored now; better kept outside the
   web root, where IIS can't serve them by mistake.
4. **`.env` files** in `workspace/docker` and `workspace/tools` (2 sites),
   including `.env.production`. Ignored now (`.env.example` stays trackable).
5. **Licence keys** (`*.lic`, `default.license.json`) are needed to run the
   modules, so they stay tracked: the repository must stay private.

Already-tracked files: none - no site is a git repository. A site that becomes
one with the old template and is then given the new one should run
`git rm --cached web.config` (and the copies above) - that stops tracking and
keeps the file on disk. Nothing was removed from any index during this audit.

## Vendors' nested .gitignore files (verified)

git applies a folder's own `.gitignore` after the root's, so the root file
can't take back what these ignore:

- `DesktopModules/DnnSharp/Common/static/.gitignore` ignores
  `bootstrap5/css/bootstrap.css` and `plant-an-app/css/papp-admin.css` - files
  the module serves. **Remove those two lines** on a site tracked in git.
- 2sxc apps' `.gitignore` ignore `bin/`, `obj/` and `.temp_cache` - only
  2sxc's compiled `ThisApp.Code.dll` and build caches on these sites, which
  2sxc makes again. Harmless.

## Validation results

| Check | Result |
|---|---|
| DNN 10.4.1's own files ignored | old 138, **new 2** (`web.config`, `App_Data/Database.mdf`) |
| DNN 9.8.1, 9.13.2 and 10.1.2's own files ignored | the same 2 in each |
| The two backup snapshots | nothing a site needs ignored - only the same kinds as on the live sites (logs, `obj/`, uploads, installable packages, backup data); removed modules' code and 2sxc's `.data` content types kept |
| Files ignored on the 8 sites | old 16,641 (4.95 GB), new 11,404 (5.02 GB) |
| Now tracked again (were wrongly ignored) | 5,599 files, 136 MB |
| Newly ignored | 226 files, 204 MB - `web.config` and copies, backups, `.env`, caches, 2sxc's compiled code |
| Code and config files (`.cs`, `.cshtml`, `.ascx`, `.js`, `.css`, `.dnn`, `.config`...) ignored by a root rule | only in `Install/Temp/` (extraction leftovers), `.vs/` and `/imagecache/Web.config` |
| Case-sensitive run | identical results |
| False positives found while building it and fixed | `Portals/0/_footer.ascx` (kept with `!Portals/[0-9]*/*.ascx`), 2sxc's markers (`*.log.resources` instead of `[Ll]og/`), `Portals/0-System` (re-included) |

Patterns: old 98, new 115 - 49 dropped, 66 added (17 of those are `!`
exceptions).

## Recommendations

- **Existing sites**: DNN Manager never overwrites a `.gitignore`, so the 7
  sites keep the old one until it's replaced by hand - worth doing before any
  of them becomes a git repository.
- **Line endings**: a site repository should carry a `.gitattributes`
  (`* -text`): DNN sites mix CRLF and LF from many vendors, and git
  normalising them would make every file differ from the server's copy.
- **Git LFS**: not needed with the new rules - the big binaries are ignored
  (packages, backups, uploads); what remains is `bin/` (about 75 MB a site)
  and a few vendor executables.
- **Backups**: keep `01_backup`-style folders and `.bacpac` files outside the
  site folder (`C:\DNN\<site>` is the web root).
- **Deploy rules**: a server connection's ignore rules
  ([`IgnoreRules.Defaults`](../src/DnnManager.Application/Deployment/IgnoreRules.cs))
  are separate and were not changed; they already keep `web.config`,
  `App_Data`, logs and caches local.

## Not covered

- File contents: no secret was read, so whether a given `web.config` copy or
  `.env` file really holds a password is assumed from its name.
- A real `git clone` of a site followed by a start in IIS was not tried; the
  rules were checked on name mirrors.
- Vendors' newer versions may add folders; the template's header says what to
  keep in mind when editing it.
