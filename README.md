# DnnManager.NET

**DNN Manager** (`dnnmgr.exe`) is a Windows desktop app for running DNN sites
locally. It sets up new projects, hosts existing folders, clones live sites over
FTP, and manages their IIS websites and databases in a shared SQL Server
container. It's a **WPF** app built on a **Clean Architecture** solution.

## Features

- **Projects** - every project folder with its site URL, IIS state, SQL status,
  database and size; open the site or folder, or remove the project.
- **New project** - download a DNN release into a new folder, with its IIS site,
  hostname and database.
- **Existing folder** - create the IIS site and/or local database for a folder
  that's already there, optionally restoring a `.bacpac` / `.bak`.
- **Clone project** - copy a site (files + database) from a local folder or an
  FTP server, including Azure SQL sources.
- **Live sites** - the saved FTP / SQL connections of your live websites, with
  **Test connection**.
- **Prerequisites** - check Docker and the IIS Windows features, and enable
  missing ones.
- **Settings** - edit `appsettings.json` from the app.
- **Light and dark theme**, a live **activity log** with Cancel, and an eye
  button on every password field.

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK** - <https://dotnet.microsoft.com/download/dotnet/10.0>
- Docker Desktop (Linux containers)
- A user account that can elevate to Administrator (UAC prompt will appear)

## Build

All commands run from `DnnManager.NET\` (the folder containing `DnnManager.csproj`).

```bash
dotnet build                # Debug   -> bin\Debug\net10.0-windows\dnnmgr.exe
dotnet build -c Release     # Release -> bin\Release\net10.0-windows\dnnmgr.exe
dotnet clean
```

## Run

The app self-elevates: launched without Administrator rights it shows a UAC
prompt and relaunches itself elevated (managing IIS needs it).

### Option A - `dotnet run` (development)

```bash
dotnet run                # Debug
dotnet run -c Release     # Release
```

Accept the UAC prompt and the window opens. `dotnet run` returns immediately
because the elevated instance is a separate process. From an elevated terminal
there's no prompt.

### Option B - run the built executable

```bash
.\bin\Release\net10.0-windows\dnnmgr.exe
```

### Option C - publish a single self-contained `.exe`

One file, no .NET runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

.\publish\dnnmgr.exe
```

`appsettings.json` and `docker-compose.yml` aren't part of the source or the
publish output. Their defaults are defined in code
([`BundledFiles.cs`](src/DnnManager.Infrastructure/Files/BundledFiles.cs)), and
the app writes them next to the exe on first start, or whenever one is missing.
Existing files are never overwritten, so your edits stick. If the folder is
read-only, the app still starts with the built-in settings.

> **"Access to the path '...\publish\dnnmgr.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

VS Code tasks for build, publish and zip are in `.vscode/tasks.json`.

## Using the app

### Window

- **Sidebar** - the pages below, **Settings** (gear icon) at the bottom, and next
  to **Projects folder** a sun / moon button that switches between the light and
  dark theme.
- **Activity** - the log at the bottom shows each step of the running operation.
  **Cancel** stops it and **Copy** puts the log on the clipboard. The chevron (or
  clicking **Activity**) collapses the log to its header bar, which still shows
  the running operation and Cancel. Click it again to bring the log back at its
  previous height.
- **Dialogs** - questions from an operation (confirmations, e.g. before dropping
  a database) open as dialogs.
- Only one operation runs at a time. While it runs, the pages stay usable
  (scrolling, browsing), but starting a second one is refused.

### Pages

| Page | What it does |
|---|---|
| **Projects** | Table of every project folder: name, site URL, IIS state, SQL status, database, size and path. **Refresh** shows it's working (button reads *Refreshing…*, a bar runs along the table) and the subtitle shows when it last updated. The table scrolls both ways - **Shift + mouse wheel** scrolls sideways. **Open site** (or double-click a row), **Open folder** and **Remove…** act on the selected project. **Reset IIS** restarts IIS (`iisreset`, after a confirmation) - e.g. after installing the URL Rewrite module when a site shows *HTTP Error 500.19*. |
| **New project** | Enter a name (validated as you type), pick the DNN release source and optionally a version (blank = latest). If a folder with that name already exists, it offers the **Existing folder** choices instead, plus downloading DNN over the folder. |
| **Existing folder** | Pick a folder, then *IIS website + local database* (the default), *local database only* or *IIS website only*, and optionally a backup to restore. See [Set up an existing project folder](#set-up-an-existing-project-folder). |
| **Clone project** | Copy a site from a local folder or an FTP server into a new project. See [Clone a project](#clone-a-project). |
| **Live sites** | The FTP and SQL connection per live website - saved when cloning over FTP, or added with **+ Add**. Edit and save them; saved passwords load masked (eye button to view). **Test connection** logs in: FTP lists the remote path, SQL opens the database itself (so contained database users work). |
| **Prerequisites** | Shows what's checked - Docker, and the IIS Windows features as a table - and **Run checks** checks them, offering to enable missing IIS features. |
| **Settings** | Edit `appsettings.json`. See [Configuration](#configuration). |

## Configuration

Settings live in the `appsettings.json` next to `dnnmgr.exe`. Edit them on the
**Settings** page and **Save**. The values are checked first (full path, valid
ports and URLs, required fields). Only the edited keys are rewritten; the rest of
the file is kept. The app reads settings at startup, so it offers to **Restart
now**. The IIS feature list and logging levels aren't on the page - **Open
appsettings.json** opens the file for those.

| Key | Meaning |
|---|---|
| `DnnManager:BaseDirectory` | Where projects live (`C:\DNN` by default). |
| `DnnManager:SitePort`, `DnnManager:HostnameSuffix` | Sites answer at `http://<project>.<HostnameSuffix>[:SitePort]`. |
| `DnnManager:Theme` | `Light`, `Dark` or `System` (follow the Windows app theme). Set by the sidebar's theme button. |
| `DnnManager:GitHubReleaseApis` | GitHub releases API URLs offered as DNN sources. |
| `DnnManager:Docker:*` | Shared SQL container: name, IP, volume, SA password, port, collation, edition, database name suffix. Keep these in sync with `docker-compose.yml`. |
| `DnnManager:RequiredIisFeatures` | IIS Windows features checked (and optionally enabled). |

Environment variables prefixed with `DNNMGR_` override settings, e.g.
`DNNMGR_DnnManager__Docker__SaPassword=...`. The Settings page lists any that
are set, since they win over what it saves.

Live site connections are stored in `connections.json` next to the exe. Passwords
are encrypted with Windows DPAPI (current user), so the file only works for the
Windows account that saved it.

## Set up an existing project folder

For a DNN site whose files are **already** in a folder under `BaseDirectory`
(copied over by hand, checked out from git, left behind by an earlier run),
**Existing folder** creates only what is missing - the files are never
downloaded, copied or overwritten.

Flow ([`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs)
→ [`HostExistingProjectUseCase`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs)):

1. **Pick the folder** - each one shows whether it already has an IIS site.
2. **Choose** *IIS website + local database* (default), *local database only*
   or *IIS website only*.
3. **Pick a backup** (when the database is included) - a `.bacpac` or `.bak`
   found in the project's `backups\` folder or its root (newest first), any file
   picked with **Browse…**, or none. Copies of files such as `web.config.bak`
   are not database backups and aren't offered.
4. **IIS website** (unless database only) - checks the IIS features, then creates
   (or recreates) the site and app pool bound to `<folder>.<HostnameSuffix>`,
   grants the IIS identities access to the folder and starts the site.
   A production `web.config` often has a URL Rewrite rule that redirects every
   request to `https://`. The local site is HTTP-only, so such rules are
   switched off (`enabled="false"`, with a *Disabled by DNN Manager* comment
   above them) and the Activity log shows a **⚠ warning** to switch them back on
   before the site is deployed to production.
5. **Database** (unless IIS only) - starts the shared SQL container. The
   database is the one `web.config` already uses on the local container, or
   otherwise `<folder>_dnndev`. Then:
   - **with a backup**, restores it (`.bacpac` via SqlPackage, `.bak` via
     `RESTORE`) - asking first if the database already exists - and points
     `dbo.PortalAlias` at the local hostname so the site answers there;
   - **without one**, keeps an existing database as it is, or creates it empty
     (run the install wizard, or restore later by running **Existing folder**
     again with *local database only* and a backup).

   Unless `web.config` already uses the local container, it then asks before
   pointing `web.config`'s `SiteSqlServer` at the database.

With the website, a database problem (e.g. Docker not running) is reported and
skipped; with *database only* it fails the run, since the database is the whole
job.

## Clone a project

**Clone project** copies an existing DNN site (files + database) into a project
under `BaseDirectory` with its own hostname, IIS site and local database.

Flow ([`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs)
→ [`CloneProjectUseCase`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Source** - *Local folder* or *FTP server*.
2. **Project and source**:
   - **Local folder** - name the new project and pick a folder under `BaseDirectory`.
   - **FTP, a live site** - reuses its saved FTP and SQL connections; choose
     *Clone - website files + database*, *Overwrite website files only* or
     *Overwrite database only*.
   - **FTP, new project** - enter host / port / user / password, **Connect &
     browse**, and double-click through the remote tree until the folder shown
     is the site root. When the clone starts, the connection is saved as a live
     site.
3. **Source database credentials** (new projects) - a saved SQL connection, the
   source's `web.config`, or a new connection (saved for the project).
4. **Copy the website files** into the project folder.
5. **Create the local database** in the shared SQL container (dropping it first
   if it exists) and seed it from the source:
   - an **Azure SQL** source is exported to a `.bacpac` (SqlPackage) and imported;
   - a source on the **local container** is backed up inside the container;
   - any **other SQL Server** is backed up with `BACKUP DATABASE`
     (`Microsoft.Data.SqlClient`) and restored with `RESTORE`.

   A copy of the backup is kept in the project's `backups\` folder.
6. **Rewrite `dbo.PortalAlias`** so portal 0's primary alias becomes the new
   hostname (see *Notes on cloning*).
7. **Point `web.config`** at the local database (supports the
   `configSource="..."` pattern; the external file is what gets rewritten). The
   site connects as the container `sa`.
8. **Create the IIS site** bound to `<name>.<HostnameSuffix>`.

### Notes on cloning

- DNN's user-facing **"Connection To The Database Failed"** page is shown for
  *any* startup exception, not just DB connection issues. When investigating,
  always read the actual exception from
  `Portals\_default\Logs\<date>.log.resources` inside the project.
- The portal-alias step
  ([`ISqlServerService.RemapPortalAliasesAsync`](src/DnnManager.Application/Abstractions/Interfaces.cs))
  rewrites the first `*.<HostnameSuffix>` alias for `PortalID = 0` into the new
  hostname, inserts one if none matched, and removes leftover stale aliases.
  Without this step the cloned site throws
  `NullReferenceException at PortalSettingsController.ConfigureActiveTab`
  because no alias matches the incoming request.

## Architecture

The solution separates UI, orchestration, IIS, Docker, GitHub, SQL Server, file
I/O and state into layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      DnnManager.Presentation                        │
│  WPF GUI: sidebar pages, activity log, dialogs, themes, settings.   │
│  Composition root (Host + DI + config), admin elevation.            │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ depends on interfaces only
┌──────────────────────────▼──────────────────────────────────────────┐
│                       DnnManager.Application                        │
│  Use cases: Setup / HostExisting / Clone / Remove / List / Prereqs. │
│  Abstractions (interfaces for IIS, Docker, SQL, Releases, Files…).  │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ implements interfaces
┌──────────────────────────▼──────────────────────────────────────────┐
│                     DnnManager.Infrastructure                       │
│  IIS (Microsoft.Web.Administration), Docker CLI, GitHub releases,   │
│  SQL (sqlcmd in the container, SqlClient + SqlPackage for remote),  │
│  FTP (FluentFTP), web.config, connections.json, appsettings.json.   │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────────┐
│                          DnnManager.Domain                          │
│  Pure POCOs / records: DnnProject, DnnRelease, DatabaseConfig,      │
│  ProjectStatus, ProjectName, Result/Result<T>. No dependencies.     │
└─────────────────────────────────────────────────────────────────────┘
```

Every arrow points *inward*: `Presentation → Application → Domain`,
`Infrastructure → Application → Domain`. Domain has zero references.

### Project layout

One `.csproj` at the root; the source is organised by layer under `src/` and
compiled into a single assembly (`dnnmgr.exe`).

```
DnnManager.NET/
├── DnnManager.csproj            ← single project (net10.0-windows, WPF WinExe)
├── app.manifest                 ← asInvoker; AdminElevation relaunches elevated
└── src/
    ├── DnnManager.Domain/
    │   ├── Models.cs            ← DnnProject, DnnRelease, DatabaseConfig, …
    │   ├── ProjectName.cs       ← project name validation
    │   └── Result.cs            ← Result / Result<T> (no exceptions across layers)
    ├── DnnManager.Application/
    │   ├── Abstractions/        ← all interfaces consumed by use cases
    │   ├── Configuration/       ← AppOptions, DockerOptions
    │   ├── UseCases/            ← one class per top-level action
    │   └── DependencyInjection.cs
    ├── DnnManager.Infrastructure/
    │   ├── Iis/                 ← IIS via Microsoft.Web.Administration
    │   ├── Docker/              ← docker compose / docker exec via ProcessRunner
    │   ├── Sql/                 ← sqlcmd in the container, remote backup, SqlPackage, connection test
    │   ├── Github/              ← GitHub API + DNN package downloader
    │   ├── Files/               ← FTP, file copy, connections.json, appsettings.json, default files (BundledFiles)
    │   ├── Projects/            ← file-system project repository
    │   ├── Prereq/              ← Docker + IIS feature checks
    │   ├── WebConfigs/          ← web.config SiteSqlServer read / write
    │   ├── Processes/           ← shared ProcessRunner
    │   └── DependencyInjection.cs
    └── DnnManager.Presentation/
        ├── Program.cs           ← composition root (Host + DI + config), starts WPF
        ├── AdminElevation.cs    ← relaunches elevated when needed
        ├── App.xaml             ← styles (buttons, inputs, lists, table, scrollbars, sidebar)
        ├── MainWindow.xaml      ← sidebar navigation + page host + activity log
        ├── Pages/               ← one page per sidebar item (incl. Settings)
        ├── Controls/            ← InputDialog, ExistingFolderOptions, PasswordInput
        ├── Themes/              ← LightTheme / DarkTheme colour palettes
        └── Services/            ← ActivityLog, OperationRunner, ThemeManager, GUI adapters
```

### Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; the UI was swapped from a terminal UI to WPF without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `IDockerService`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `ISqlConnectionTester`, `IUserPrompt`, `IProgressReporter`, … Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; unexpected exceptions are still logged and surfaced centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI, configuration binding (`appsettings.json` + `DNNMGR_*` env vars), logging via `Microsoft.Extensions.Logging`. |
| **WPF, code-behind pages** | One `UserControl` per sidebar item, rebuilt on each visit so lists (folders, backups, live sites) are always fresh. |
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs, and backs the log's **Cancel** button. |
| **Adapters for GUI → app layer** | `GuiProgressReporter` (writes to the activity log) and `GuiUserPrompt` (modal dialogs) implement application interfaces, so use cases never know what drives them. |
| **Runtime theming** | Colours live in `LightTheme` / `DarkTheme`; everything references them with `DynamicResource`, and `ThemeManager` swaps the dictionary (and the title bar's dark mode) live. |
| **SQL** | The local container is driven with `sqlcmd` via `docker exec`; remote / Azure SQL uses `Microsoft.Data.SqlClient` and SqlPackage (`.bacpac`). |
| **Centralised error handling** | `OperationRunner` catches per-action exceptions and reports them in the activity log; `App` shows anything escaping a click handler; `Program.cs` catches fatal errors. |
| **Admin enforcement** | `AdminElevation` relaunches the app elevated (UAC prompt) when it isn't. |
| **No hardcoded values** | Container name, SA password, port, GitHub APIs, IIS feature list, hostname suffix, base directory, theme - all in `appsettings.json`. |

## Extending

- **New page**: add a `UserControl` under `Pages/` (Presentation) that runs its
  use case (Application, registered with DI) through `OperationRunner`, then add
  a sidebar entry in `MainWindow.xaml` and its type to the `Pages` map in
  `MainWindow.xaml.cs`. Reference colours with `DynamicResource` so the page
  follows the theme.
- **New colour**: add the same key to both `Themes/LightTheme.xaml` and
  `Themes/DarkTheme.xaml`.
- **Add tests**: every use case takes pure interfaces - drop in fakes / mocks
  (no test project is shipped).

## Component map

| Area | C# location |
|---|---|
| Main window / navigation / activity log | [`MainWindow.xaml`](src/DnnManager.Presentation/MainWindow.xaml) |
| Projects list + remove | [`ProjectsPage`](src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs), [`UseCases/ListProjectsUseCase.cs`](src/DnnManager.Application/UseCases/ListProjectsUseCase.cs), [`UseCases/RemoveProjectUseCase.cs`](src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs) |
| New project | [`SetupPage`](src/DnnManager.Presentation/Pages/SetupPage.xaml.cs) + [`UseCases/SetupProjectUseCase.cs`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs) |
| Existing folder (IIS / DB) | [`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs) + [`UseCases/HostExistingProjectUseCase.cs`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |
| Clone project | [`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs) + [`UseCases/CloneProjectUseCase.cs`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) |
| Live sites (saved connections) | [`ConnectionsPage`](src/DnnManager.Presentation/Pages/ConnectionsPage.xaml.cs), [`Files/ConnectionProfileStore.cs`](src/DnnManager.Infrastructure/Files/ConnectionProfileStore.cs), [`Sql/SqlConnectionTester.cs`](src/DnnManager.Infrastructure/Sql/SqlConnectionTester.cs) |
| Prerequisites | [`PrerequisitesPage`](src/DnnManager.Presentation/Pages/PrerequisitesPage.xaml.cs) + [`UseCases/CheckPrerequisitesUseCase.cs`](src/DnnManager.Application/UseCases/CheckPrerequisitesUseCase.cs) |
| Settings | [`SettingsPage`](src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs) + [`Files/AppSettingsFile.cs`](src/DnnManager.Infrastructure/Files/AppSettingsFile.cs) |
| Themes | [`Themes/`](src/DnnManager.Presentation/Themes/), [`Services/ThemeManager.cs`](src/DnnManager.Presentation/Services/ThemeManager.cs) |
| FTP browse / copy | [`Files/FtpBrowser.cs`](src/DnnManager.Infrastructure/Files/FtpBrowser.cs), [`Files/ProjectFileCopier.cs`](src/DnnManager.Infrastructure/Files/ProjectFileCopier.cs) |
| GitHub release lookup | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| Docker / sqlcmd | [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs), [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Default `appsettings.json` / `docker-compose.yml` | [`Files/BundledFiles.cs`](src/DnnManager.Infrastructure/Files/BundledFiles.cs) - written next to the exe when missing |
| Shared SQL container | `docker-compose.yml` next to the exe, brought up via [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs) (`ComposeUpAsync`) |

## Notes / limitations

- The dark title bar needs Windows 10 20H1 or later; older versions keep a light one.
- Yes / No confirmation boxes and file pickers are standard Windows dialogs and
  follow the Windows theme, not the app's.
- `connections.json` passwords are tied to the Windows account that saved them
  (DPAPI) - copying the file to another account or machine loses them.
