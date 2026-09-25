# Changelog

All notable changes to DnnManager.NET are documented here.

## Unreleased

### Added

- **Desktop GUI replaces the terminal UI.** `dnnmgr.exe` is now a WPF app:
  a sidebar with every former menu action - projects overview (open site /
  folder, remove), new project, existing folder, clone (local or FTP with a
  folder browser), database backup / overwrite, saved connections and
  prerequisites - and a live activity log with Cancel. Use-case questions open
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
  that already exists now offers "IIS website only", "IIS website + database",
  "database only", or downloading DNN over it, instead of asking to overwrite
  halfway through.
- **Missing `appsettings.json` / `docker-compose.yml` are recreated.** Both
  defaults are compiled into `dnnmgr.exe`; if either is missing from next to
  the exe, the app writes the default at startup (and again before
  `docker compose up`) instead of failing to start. Existing files are never
  overwritten.

### Removed

- **The terminal UI** and its `DnnManager:Console` window-size settings
  (ignored if still present in an existing `appsettings.json`).
- **Database (backup / overwrite).** The action and the code behind it
  (`ExportDatabaseUseCase`, `ImportDatabaseUseCase`, `IRemoteSqlAdminService`)
  are gone. To load a backup into a local project's database, use **Existing
  folder** with **local database only**.

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
