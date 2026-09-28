# Changelog

All notable changes to DnnManager.NET are documented here.

## v2.0.0 - 2026-09-28

A major release: projects can be imported from and exported to a `.zip` +
`.bacpac`, the shared SQL Server's Docker container is set up from Settings,
and the Projects table gets a right-click menu. FTP and the Live sites page are
removed, and the app checks the SQL Server connection instead of Docker.

### Upgrading from v1.0.x

- `connections.json` (saved FTP / SQL connections of live sites) is no longer
  used and can be deleted.
- DNN Manager no longer starts the SQL container when a project needs it. If it
  isn't running, start it with **Settings → Set up Docker container** - that
  also rewrites `docker-compose.yml` next to the app from the settings (without
  the old fixed `dnn_network` subnet). An existing data volume keeps its data
  and its sa password.

### Added

- **Import a site .zip as a new project.** New project has a **Start from**
  choice: a new site (download a DNN release, as before) or an existing site -
  a `.zip` of its files plus its database `.bacpac` (required; a `.bak` works
  too). The zip is extracted into the new folder (the site root is found by its
  `web.config`), then the site is hosted like Host project: IIS website,
  restored database, portal alias and `web.config`.
- **Reset IIS.** A button on the Projects page restarts IIS (`iisreset`) after
  a confirmation - for stuck sites, or to pick up IIS changes such as a newly
  installed URL Rewrite module behind a 500.19 error.
- **HTTPS redirects are switched off for local sites.** Host project switches
  off `web.config` rewrite rules that redirect to `https://`
  (the local site is HTTP-only, so it would never load), marks them with a
  *Disabled by DNN Manager* comment, and shows a ⚠ warning in the Activity log -
  a new warning style - to switch them back on before deploying to production.
- **Selectable activity log.** Text in the Activity log can be selected and
  copied, across lines too (mouse, Ctrl+A / Ctrl+C, right-click Copy). The
  **Copy** button copies the selection, or the whole log when nothing is selected.
- **Copy path and Export in the project menu.** **Copy path** puts the project
  folder on the clipboard. **Export** saves the project as the pair New project
  imports: a `.zip` of the site files (without `backups` and `.git`; files the
  running site holds open are read too) and a `.bacpac` of its database next to
  it - or only the site files, or only the database (a submenu with the three
  choices).
- **Project right-click menu.** Right-click a project on the Projects page for
  **Details…** (folder, site, IIS, SQL, database, `web.config` connection, DNN
  version, size, solution, git branch, backups) and **Open in …** for each IDE
  installed on the PC - Visual Studio (found with `vswhere`; opens the project's
  `.sln` when there is exactly one), VS Code, VS Code Insiders, Cursor,
  Windsurf, Rider and Sublime Text - next to Open site / Open folder / Remove.
  A right-click selects the row under the mouse; the menu follows the theme.
- **Set up the Docker container from Settings.** **Set up Docker container**
  writes `docker-compose.yml` from the SQL Server settings (container name, sa
  password, edition, collation, port, volume), runs `docker compose up -d` with
  live progress in the Activity log and waits until SQL Server accepts the sa
  login. **Show docker-compose.yml** shows the generated file and whether the one
  next to the app matches; replacing a different one asks first. The card is
  now called **SQL Server**.
- **Test the SQL Server connection in Settings.** **Test connection** logs in as
  `sa` with the IP, port and password in the form, before saving them.

### Changed

- **Open folder moved to the right-click menu** - the Projects toolbar keeps
  Open site, Remove, Reset IIS and Refresh.
- **Settings uses the full window width.**
- **`docker-compose.yml` is generated from the settings** and no longer defines
  a fixed network / subnet, which clashed with other compose projects ("Pool
  overlaps with other one on this address space").
- **Projects table shows the DNN version** (from `bin\DotNetNuke.dll`, e.g.
  `9.13.4`) in place of the SQL column.
- **"Existing folder" is now "Host project".** Its options say *database*
  instead of *local database* - the database can be on any SQL Server, and the
  prompts name the actual server.
- **SQL Server connection check replaces the Docker check.** Prerequisites,
  new project, host project, clone and the Projects page now log in to the
  SQL Server from Settings (`ContainerIp,DefaultPort` as `sa`, 5s timeout)
  instead of querying Docker. The project flows no longer start the container
  (`docker compose up` / `docker start`) - start it once with **Set up Docker
  container** in Settings; when it isn't reachable the database steps are
  skipped or fail with a clear message.

### Removed

- **FTP.** Cloning over FTP and the FTP folder browser are gone (along with the
  FluentFTP package). Clone copies from a local folder.
- **Live sites page and saved connections.** The page and `connections.json`
  are no longer used. Clone takes the source database from the site's
  `web.config`, or from a connection entered for that clone (not saved).

## v1.0.3 - 2026-09-25

The terminal UI is replaced by a desktop app, **DNN Manager**. Existing
projects, `appsettings.json` and `connections.json` keep working as they are.

### Added

- **Desktop GUI replaces the terminal UI.** `dnnmgr.exe` is now a WPF app:
  a sidebar with the actions - projects overview (open site / folder, remove),
  new project, existing folder, clone (local or FTP with a folder browser),
  live sites and prerequisites - and a live activity log with Cancel. Use-case questions open
  as dialogs. The use cases themselves are unchanged. The app is now called
  **DNN Manager** (window title, sidebar and dialogs).
- **Settings page.** The gear icon at the bottom of the sidebar edits
  `appsettings.json` (projects folder, site port, hostname suffix, DNN release
  sources, SQL container settings), validates the values and offers to restart
  so they apply. Other keys in the file are kept as they are.
- **Live sites (was "Saved connections").** The page is renamed and has a **Test
  connection** button for FTP (logs in and lists the remote path) and SQL
  (logs in to the database itself, so contained users work too). **+ Add**
  creates connections for another project ahead of cloning it. The saved
  password now loads into the (masked) box instead of "blank = keep current".
- **Light and dark theme.** A sun / moon button next to "Projects folder" in
  the sidebar switches the whole app live, including the sidebar, the activity
  log, inputs, lists, the projects table, scrollbars and the window title bar. The choice is saved as
  `Theme` in `appsettings.json`; by default the app follows the Windows app theme.
- **Show / hide passwords.** Every password field has an eye button.
- **Hideable activity log.** The chevron in the Activity header collapses the
  log to its header bar (still showing the running operation and Cancel), and
  brings it back at its previous height.
- **Setup an existing project folder.** A new action for a DNN site
  whose files are already under `BaseDirectory`: it creates the IIS website, a
  local database, or both - without downloading, copying or overwriting any
  files. When a database is included you pick a `.bacpac` (or `.bak`) to
  restore - from the project's `backups\` folder or root, or any path - or none
  for an empty database. It uses the database `web.config` already points at
  on the local container, or `<name>_dnndev`, and asks before repointing
  `web.config`. A restore remaps the portal alias to the local hostname, and
  an existing database is only replaced after you confirm.
- **Setup detects an existing folder up front.** Typing the name of a folder
  that already exists now offers "IIS website + local database" (the default),
  "local database only", "IIS website only", or downloading DNN over it,
  instead of asking to overwrite halfway through.
- **`appsettings.json` / `docker-compose.yml` are generated.** Their defaults
  are defined in code (`BundledFiles.cs`), so neither file is needed in the
  source tree or the publish folder - the build no longer fails when they're
  missing. The app writes them next to the exe on first start (and the compose
  file again before `docker compose up`); existing files are never overwritten,
  and if `appsettings.json` can't be written the built-in settings are used.

### Removed

- **The terminal UI** and its `DnnManager:Console` window-size settings
  (ignored if still present in an existing `appsettings.json`).
- **Database (backup / overwrite).** The action and the code behind it
  (`ExportDatabaseUseCase`, `ImportDatabaseUseCase`, `IRemoteSqlAdminService`)
  are gone. To load a backup into a local project's database, use **Existing
  folder** with **local database only**.
- **Unused code.** Docker helpers nothing called (`DoesContainerExistAsync`,
  `ComposeDownAsync`, `ExecAsync`), `ListConfiguredProjects`, the
  `DockerSettings` / `IisFeature` records, unused fields
  (`DatabaseConfig.RemoteBackupDirectory`, `ProjectStatus.DatabaseUser`), the
  SqlPackage `IsAvailable` / `InstallHint` interface members and import
  `properties` option, and loggers that were never written to. No behaviour change.

### Fixed

- **Copied config files are no longer offered as database backups.** Files
  like `web.config.bak` matched the `.bak` filter, so setting up an existing
  folder could pre-select one as the backup to restore. Only real `.bak` /
  `.bacpac` database backups are listed and accepted now.

- **Setup no longer fails outright when Docker isn't installed.** Launching a
  missing executable threw instead of returning a failed result, so the
  intended "Docker not found - skipping the database" path never ran; the whole
  setup aborted with "The system cannot find the file specified".
- **URLs honour `SitePort`.** Setup and clone printed, and probed,
  `http://<host>` even when the site was bound to a different port.
- A failure to grant IIS folder permissions is now reported instead of ignored.
- The "no configured projects" message referred to `docker-compose.yml`; it now
  says `web.config`, which is what is actually checked.

### Performance

- **IIS feature check: one PowerShell process instead of 16.** Every feature
  was queried in its own `powershell.exe` with its own DISM module load
  (~0.8 s each here); all features are now queried, and enabled, in one run.
- **Docker check: one call instead of two.** `docker version` replaces
  `docker --version` + `docker info` (~1.3 s -> ~0.3 s measured here).
- **Fewer `docker` calls when preparing SQL.** Container existence and state
  come from one `docker ps`, and clone no longer re-queries the container to
  decide whether its source database is local.
- **IIS sites are torn down once, not twice.** Setup and clone called
  `RemoveSite` right before `CreateSite`, which already removes the old site
  and waits for its worker process to exit.

### Changed

- **Projects page.** **Refresh** shows it's working (*Refreshing…*, a loading
  bar, faded rows) and the subtitle shows when the list last updated. Columns
  size to their content and the table scrolls sideways (scrollbar or
  Shift + mouse wheel). Pages stay scrollable while an operation runs.
- **Prerequisites** lists the IIS Windows features as a table.
- New project, Clone and Prerequisites use the full window width, and plain text
  follows the theme colour (it was unreadable in dark mode).
- Setup, clone and the new action share one implementation of the IIS-site and
  SQL-container steps (`Provisioning.cs`), and one definition of the hostname,
  site URL, database-name and server conventions (`AppOptions`).

## v1.0.2 - 2026-09-03

A hardening and performance patch. No new features and no configuration
changes: existing projects, `appsettings.json` and `connections.json` all keep
working as they are.

### Fixed

- **Project names are now validated.** The name typed at "Setup a new DNN
  project" and "Clone a DNN project" was taken as free text and used to build a
  folder under `BaseDirectory`, an IIS site and application pool, a host header
  and a database name. A name containing `..` or a path separator resolved
  *outside* `C:\DNN` - and removing that project deletes the resolved path
  recursively. Names are now restricted to a single safe path segment (letters,
  digits, `-`, `_`, `.`), and both screens re-prompt on a bad name instead of
  failing later in the run. Existing names such as `metro_test` are unaffected.
- **Database names are quoted before reaching SQL Server.** They were
  interpolated straight into T-SQL, and the name a site actually uses is read
  out of its `web.config` - so it is not necessarily one this tool created.
- **Cancelling no longer orphans child processes.** Pressing Ctrl+C abandoned
  only the wait, leaving `docker`, `sqlcmd` and `powershell` running with
  container locks and file handles still held. The process tree is now stopped.
- **`connections.json` can no longer be lost.** It was rewritten in place, so a
  crash partway through truncated every saved credential; and an unreadable file
  silently started an empty store that the next save overwrote. Saves are now
  write-then-rename, and a damaged file is kept aside as `connections.json.corrupt`.
- **The FTP connection is always closed**, not just when a clone succeeds.
- The directory-size total for a project no longer collapses to 0 MB because of
  one unreadable subfolder.

### Performance

- **Project listing is faster.** "Show all projects info" built two
  `ServerManager` instances per project - each one loading IIS's
  `applicationHost.config` - and sized every site serially with one file-system
  call per file. It now takes a single IIS snapshot for all sites and sizes the
  projects in parallel, reading sizes from the directory scan itself. Measured
  on three real sites: ~60 ms down to ~32 ms warm, with identical totals.
- **Copying website files is faster**, most noticeably on large sites. Every
  single file triggered a `CreateDirectory` call and repainted a full-width
  console progress line - on an 18,000-file site the progress display cost more
  than the copy. Folders are now created once each and progress refreshes about
  ten times a second, in both the local-folder and FTP paths.

### Changed

- The build now stamps a version, so `dnnmgr.exe` reports 1.0.2 in its file
  properties instead of 1.0.0.0.

## v1.0.1

- Added `.gitignore` and the zip task to `.vscode/tasks.json`.

## v1.0.0

- First tagged release: VS Code tasks for building, cleaning and publishing.
