# DnnManager.NET

**DNN Manager** (`dnnmgr.exe`) is a Windows desktop app for running DNN sites
locally. It sets up new projects, imports and exports sites as a `.zip` +
`.bacpac`, hosts existing folders, clones sites from a local folder, and manages
their IIS websites and databases in a shared SQL Server (a Docker container it
can set up for you). It's a **WPF** app built on a **Clean Architecture**
solution.

## Features

- **Projects** - every project folder with its site URL, DNN version, IIS
  state, database and size. Right-click a project for its details, to open it in
  an installed IDE, copy its path, export it or remove it.
- **New project** - download a DNN release into a new folder, or import a `.zip`
  of an existing site plus its `.bacpac`, with its IIS site, hostname and
  database.
- **Host project** - create the IIS site and/or database for a folder
  that's already there, optionally restoring a `.bacpac` / `.bak`.
- **Clone project** - copy a site (files + database) from a local folder,
  including Azure SQL sources.
- **Prerequisites** - check the SQL Server connection and the IIS Windows
  features, and enable missing ones.
- **Settings** - edit `appsettings.json` from the app, test the SQL Server
  connection and set up its Docker container.
- **Light and dark theme**, a live **activity log** with Cancel, and an eye
  button on every password field.

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK** - <https://dotnet.microsoft.com/download/dotnet/10.0>
- Docker Desktop (Linux containers) for the shared SQL Server - set it up once
  with **Settings → Set up Docker container** (or point the SQL Server settings
  at a SQL Server you already run)
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
| **Projects** | Table of every project folder: name, site URL, DNN version (from `bin\DotNetNuke.dll`), IIS state, database, size and path. **Refresh** shows it's working (button reads *Refreshing…*, a bar runs along the table) and the subtitle shows when it last updated. The table scrolls both ways - **Shift + mouse wheel** scrolls sideways. **Open site** (or double-click a row) and **Remove…** act on the selected project; **right-click** a project for everything else (see [Project menu](#project-menu)). **Reset IIS** restarts IIS (`iisreset`, after a confirmation) - e.g. after installing the URL Rewrite module when a site shows *HTTP Error 500.19*. |
| **New project** | Enter a name (validated as you type), then **Start from**: *a new site* - pick the DNN release source and optionally a version (blank = latest) - or *an existing site* - pick the site `.zip` and its database `.bacpac` (see [Import a site .zip](#import-a-site-zip)). If a folder with that name already exists, it offers the **Host project** choices instead, plus downloading DNN over the folder. |
| **Host project** | Pick a folder, then *IIS website + database* (the default), *database only* or *IIS website only*, and optionally a backup to restore. See [Host a project](#host-a-project). |
| **Clone project** | Copy a site from a local folder into a new project. See [Clone a project](#clone-a-project). |
| **Prerequisites** | Shows what's checked - the SQL Server connection, and the IIS Windows features as a table - and **Run checks** checks them, offering to enable missing IIS features. |
| **Settings** | Edit `appsettings.json`. Under **SQL Server**, **Test connection** logs in with the values in the form (saved or not), **Set up Docker container** writes `docker-compose.yml` from those values, runs `docker compose up -d` (image download progress in the Activity log) and waits until SQL Server accepts the sa login, and **Show docker-compose.yml** shows the file those values produce and whether the one next to the app matches. See [Configuration](#configuration). |

### Project menu

Right-click a project on the **Projects** page (or select it and press the
Menu key):

| Item | What it does |
|---|---|
| **Details…** | Folder, site, IIS state, SQL Server status, database, `web.config` connection (without the password), DNN version, size, solution file(s), git branch and backups - selectable, with **Copy all**. |
| **Open site** / **Open folder** | The site in the browser / the folder in Explorer. |
| **Copy path** | Puts the project folder on the clipboard. |
| **Open in …** | One entry per IDE found on the PC: Visual Studio (via `vswhere`; opens the project's `.sln` when it has exactly one), VS Code, VS Code Insiders, Cursor, Windsurf, Rider and Sublime Text. |
| **Export** ▸ | *Site and database* - a `.zip` of the site files (without `backups` and `.git`) plus a `.bacpac` of its database next to it, the pair **New project** imports - or just the *site files* (`.zip`) or the *database* (`.bacpac`). |
| **Remove…** | After a confirmation, removes the IIS site and deletes the project folder - and drops its database if you say so. |

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
| `DnnManager:Docker:*` | Shared SQL Server: IP, port and SA password to connect with; container name, volume, collation and edition for the Docker container; database name suffix. `docker-compose.yml` is generated from these by **Set up Docker container**. An existing data volume keeps the sa password it was created with. |
| `DnnManager:RequiredIisFeatures` | IIS Windows features checked (and optionally enabled). |

Environment variables prefixed with `DNNMGR_` override settings, e.g.
`DNNMGR_DnnManager__Docker__SaPassword=...`. The Settings page lists any that
are set, since they win over what it saves.

## Import a site .zip

**New project → Start from: an existing site** creates a project from a zipped
DNN site and its database backup - both required
([`ImportProjectUseCase`](src/DnnManager.Application/UseCases/ImportProjectUseCase.cs)):

1. **Extract** the zip into a new folder `<BaseDirectory>\<name>`. The site root
   is the zip's shallowest folder with a `web.config`, so a zip with everything
   under one top folder works too; files outside it are skipped. Entries that
   would land outside the folder are refused, and a failed or cancelled
   extraction removes the folder again. The zip isn't searched for a database.
2. **Host it** exactly like [Host a project](#host-a-project) with *IIS website +
   database*: IIS site, HTTPS redirects switched off, the `.bacpac` (a `.bak`
   works too) restored,
   `dbo.PortalAlias` pointed at the local hostname and `web.config` pointed at the
   database (after asking).

## Host a project

For a DNN site whose files are **already** in a folder under `BaseDirectory`
(copied over by hand, checked out from git, left behind by an earlier run),
**Host project** creates only what is missing - the files are never
downloaded, copied or overwritten.

Flow ([`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs)
→ [`HostExistingProjectUseCase`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs)):

1. **Pick the folder** - each one shows whether it already has an IIS site.
2. **Choose** *IIS website + database* (default), *database only*
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
5. **Database** (unless IIS only) - checks the SQL Server connection. The
   database is the one `web.config` already uses on the local container, or
   otherwise `<folder>_dnndev`. Then:
   - **with a backup**, restores it (`.bacpac` via SqlPackage, `.bak` via
     `RESTORE`) - asking first if the database already exists - and points
     `dbo.PortalAlias` at the local hostname so the site answers there;
   - **without one**, keeps an existing database as it is, or creates it empty
     (run the install wizard, or restore later by running **Host project**
     again with *database only* and a backup).

   Unless `web.config` already uses the local container, it then asks before
   pointing `web.config`'s `SiteSqlServer` at the database.

With the website, a database problem (e.g. SQL Server not reachable) is reported and
skipped; with *database only* it fails the run, since the database is the whole
job.

## Clone a project

**Clone project** copies an existing DNN site (files + database) into a project
under `BaseDirectory` with its own hostname, IIS site and local database.

Flow ([`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs)
→ [`CloneProjectUseCase`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Project and source** - name the new project and pick the source folder
   under `BaseDirectory`.
2. **Source database credentials** - the source's `web.config`, or another SQL
   connection entered for this clone (not saved).
3. **Copy the website files** into the project folder.
4. **Check the local SQL Server** - log in as `sa` at `ContainerIp,DefaultPort`.
   If it doesn't answer, the database steps are skipped and the files are kept.
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
│  Use cases: Setup / Import / Export / HostExisting / Clone /        │
│  Remove / List / Prereqs / SetupSqlContainer.                       │
│  Abstractions (interfaces for IIS, SQL, Releases, Files…).          │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ implements interfaces
┌──────────────────────────▼──────────────────────────────────────────┐
│                     DnnManager.Infrastructure                       │
│  IIS (Microsoft.Web.Administration), Docker CLI, GitHub releases,   │
│  SQL (sqlcmd in the container, SqlClient + SqlPackage for remote),  │
│  web.config, appsettings.json.                                      │
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
    │   ├── Docker/              ← docker compose up for the shared SQL container
    │   ├── Sql/                 ← sqlcmd in the container, remote backup, SqlPackage, connection test
    │   ├── Github/              ← GitHub API + DNN package downloader
    │   ├── Files/               ← file copy, site .zip import / export, appsettings.json, default files (BundledFiles)
    │   ├── Projects/            ← file-system project repository
    │   ├── Prereq/              ← IIS feature checks
    │   ├── WebConfigs/          ← web.config SiteSqlServer read / write
    │   ├── Processes/           ← shared ProcessRunner
    │   └── DependencyInjection.cs
    └── DnnManager.Presentation/
        ├── Program.cs           ← composition root (Host + DI + config), starts WPF
        ├── AdminElevation.cs    ← relaunches elevated when needed
        ├── App.xaml             ← styles (buttons, inputs, lists, table, scrollbars, sidebar)
        ├── MainWindow.xaml      ← sidebar navigation + page host + activity log
        ├── Pages/               ← one page per sidebar item (incl. Settings)
        ├── Controls/            ← InputDialog, DetailsDialog, ExistingFolderOptions, PasswordInput
        ├── Themes/              ← LightTheme / DarkTheme colour palettes
        └── Services/            ← ActivityLog, OperationRunner, ThemeManager, IdeLocator, GUI adapters
```

### Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; the UI was swapped from a terminal UI to WPF without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `ISqlConnectionTester`, `IUserPrompt`, `IProgressReporter`, … Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; unexpected exceptions are still logged and surfaced centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI, configuration binding (`appsettings.json` + `DNNMGR_*` env vars), logging via `Microsoft.Extensions.Logging`. |
| **WPF, code-behind pages** | One `UserControl` per sidebar item, rebuilt on each visit so lists (folders, backups) are always fresh. |
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs, and backs the log's **Cancel** button. |
| **Adapters for GUI → app layer** | `GuiProgressReporter` (writes to the activity log) and `GuiUserPrompt` (modal dialogs) implement application interfaces, so use cases never know what drives them. |
| **Runtime theming** | Colours live in `LightTheme` / `DarkTheme`; everything references them with `DynamicResource`, and `ThemeManager` swaps the dictionary (and the title bar's dark mode) live. |
| **SQL** | The local container is checked by logging in with `Microsoft.Data.SqlClient` and driven with `sqlcmd` via `docker exec`; remote / Azure SQL uses `Microsoft.Data.SqlClient` and SqlPackage (`.bacpac`). |
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
| Projects list, remove, export | [`ProjectsPage`](src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs), [`UseCases/ListProjectsUseCase.cs`](src/DnnManager.Application/UseCases/ListProjectsUseCase.cs), [`UseCases/RemoveProjectUseCase.cs`](src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs), [`UseCases/ExportProjectUseCase.cs`](src/DnnManager.Application/UseCases/ExportProjectUseCase.cs) |
| New project | [`SetupPage`](src/DnnManager.Presentation/Pages/SetupPage.xaml.cs) + [`UseCases/SetupProjectUseCase.cs`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs), [`UseCases/ImportProjectUseCase.cs`](src/DnnManager.Application/UseCases/ImportProjectUseCase.cs) |
| Host project (IIS / DB) | [`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs) + [`UseCases/HostExistingProjectUseCase.cs`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |
| Clone project | [`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs) + [`UseCases/CloneProjectUseCase.cs`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) |
| SQL connection test | [`Sql/SqlConnectionTester.cs`](src/DnnManager.Infrastructure/Sql/SqlConnectionTester.cs) |
| Projects right-click menu / IDE detection | [`ProjectsPage`](src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs), [`Services/IdeLocator.cs`](src/DnnManager.Presentation/Services/IdeLocator.cs) |
| Prerequisites | [`PrerequisitesPage`](src/DnnManager.Presentation/Pages/PrerequisitesPage.xaml.cs) + [`UseCases/CheckPrerequisitesUseCase.cs`](src/DnnManager.Application/UseCases/CheckPrerequisitesUseCase.cs) |
| Settings (incl. SQL test, Docker setup) | [`SettingsPage`](src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs) + [`Files/AppSettingsFile.cs`](src/DnnManager.Infrastructure/Files/AppSettingsFile.cs) |
| Themes | [`Themes/`](src/DnnManager.Presentation/Themes/), [`Services/ThemeManager.cs`](src/DnnManager.Presentation/Services/ThemeManager.cs) |
| File copy, zip extract / create | [`Files/ProjectFileCopier.cs`](src/DnnManager.Infrastructure/Files/ProjectFileCopier.cs) |
| GitHub release lookup | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| sqlcmd | [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Default `appsettings.json` / `docker-compose.yml` | [`Files/BundledFiles.cs`](src/DnnManager.Infrastructure/Files/BundledFiles.cs) - written next to the exe when missing |
| Shared SQL container | `docker-compose.yml` next to the exe, generated by `BundledFiles.ComposeFor` and brought up by [`Docker/DockerComposeService.cs`](src/DnnManager.Infrastructure/Docker/DockerComposeService.cs) + [`UseCases/SetupSqlContainerUseCase.cs`](src/DnnManager.Application/UseCases/SetupSqlContainerUseCase.cs); the connection check is `LocalSqlContainer` in [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |

## Notes / limitations

- The dark title bar needs Windows 10 20H1 or later; older versions keep a light one.
- Yes / No confirmation boxes and file pickers are standard Windows dialogs and
  follow the Windows theme, not the app's.
- The app runs as Administrator, so an IDE opened from the project menu does too
  (VS Code shows *[Administrator]* in its title).
- The SQL Server keeps the sa password its data volume was created with -
  changing **SA password** in Settings doesn't change it in an existing
  container.
- Cloning from a SQL Server on another machine (not Azure SQL) writes the
  `.bak` on that server, in this PC's temp path, so it only works when the source
  server runs on this machine. Azure SQL sources go through a `.bacpac` and work
  from anywhere.
