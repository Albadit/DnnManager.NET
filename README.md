# DnnManager.NET

A production-ready DNN management tool, with a **Clean Architecture** solution
and a **WPF desktop GUI**.

## Architecture

The solution is built around clear boundaries that separate UI, orchestration, IIS,
Docker, GitHub, SQL Server, file I/O and state into maintainable layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      DnnManager.Presentation                        │
│  WPF GUI (sidebar pages, activity log, dialogs, settings), DI,      │
│  configuration loading, structured logging, hosting, admin check.   │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ depends on interfaces only
┌──────────────────────────▼──────────────────────────────────────────┐
│                       DnnManager.Application                        │
│  Use cases (Setup / Remove / List / CheckPrereqs / Export / Import) │
│  Abstractions (interfaces for IIS, Docker, SQL, Releases, Files…).  │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ implements interfaces
┌──────────────────────────▼──────────────────────────────────────────┐
│                     DnnManager.Infrastructure                       │
│  IIS (Microsoft.Web.Administration), Docker CLI (Process),          │
│  GitHub releases (HttpClient), SQL Server (sqlcmd in container),    │
│  web.config & filesystem repository, prerequisite checks.           │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
┌──────────────────────────▼──────────────────────────────────────────┐
│                          DnnManager.Domain                          │
│  Pure POCOs / records: DnnProject, DnnRelease, DatabaseConfig,      │
│  ProjectStatus, Result/Result<T>. No dependencies.                  │
└─────────────────────────────────────────────────────────────────────┘
```

### Dependency rule
Every arrow points *inward*: `Presentation → Application → Domain`,
`Infrastructure → Application → Domain`. Domain has zero references.

## Project layout

One `.csproj` at the root. Source is still organised by layer under `src/` for
clarity, but the SDK globs every `*.cs` into a single assembly (`dnnmgr.exe`).

```
DnnManager.NET/
├── DnnManager.csproj            ← single project (net10.0-windows, WPF WinExe)
├── app.manifest                 ← asInvoker; AdminElevation relaunches elevated
├── appsettings.json             ← all tunables (no hardcoded constants in code)
├── README.md
└── src/
    ├── DnnManager.Domain/
    │   ├── Models.cs            ← DnnProject, DnnRelease, DatabaseConfig, …
    │   └── Result.cs            ← Result / Result<T> (no exceptions across layers)
    ├── DnnManager.Application/
    │   ├── Abstractions/        ← all interfaces consumed by use cases
    │   ├── Configuration/       ← AppOptions, DockerOptions
    │   ├── UseCases/            ← one class per top-level action
    │   └── DependencyInjection.cs
    ├── DnnManager.Infrastructure/
    │   ├── Iis/                 ← IIS via Microsoft.Web.Administration
    │   ├── Docker/              ← docker compose / docker exec via ProcessRunner
    │   ├── Sql/                 ← sqlcmd-in-container (no SqlClient dependency)
    │   ├── Github/              ← GitHub API + DNN package downloader
    │   ├── Projects/            ← FS-backed project repository
    │   ├── Prereq/              ← Docker + IIS feature checks
    │   ├── Processes/           ← shared ProcessRunner
    │   └── DependencyInjection.cs
    └── DnnManager.Presentation/
        ├── Program.cs           ← composition root (Host + DI + config), starts WPF
        ├── AdminElevation.cs    ← relaunches elevated when needed
        ├── App.xaml             ← theme (colours, buttons, cards, sidebar)
        ├── MainWindow.xaml      ← sidebar navigation + page host + activity log
        ├── Pages/               ← one page per sidebar item (incl. Settings)
        ├── Controls/            ← InputDialog, ExistingFolderOptions, PasswordInput
        ├── Themes/              ← LightTheme / DarkTheme palettes (swapped by ThemeManager)
        └── Services/            ← ActivityLog, OperationRunner, ThemeManager, GUI adapters
```

The Clean Architecture **dependency rule still holds** at the namespace level
(`DnnManager.Presentation` → `DnnManager.Application` → `DnnManager.Domain`,
`DnnManager.Infrastructure` → `DnnManager.Application` → `DnnManager.Domain`),
it's just no longer enforced by project boundaries. Keep new code in the
correct `src/<layer>` folder.

## Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; the UI was swapped from a terminal UI to WPF without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `IDockerService`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `IHttpConnectivityChecker`, `IUserPrompt`, `IProgressReporter`. Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; we still log and surface unexpected exceptions centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI, configuration binding (`appsettings.json` + `DNNMGR_*` env vars), structured logging via `Microsoft.Extensions.Logging`. |
| **WPF GUI, code-behind pages** | One `UserControl` per sidebar item, rebuilt on each visit so lists (folders, backups, live sites) are always fresh. |
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs and backs the log's **Cancel** button. |
| **Adapter pattern for GUI → app layer** | `GuiProgressReporter` (writes to the activity log) and `GuiUserPrompt` (modal dialogs) implement application interfaces, so use cases never know what drives them. |
| **`net10.0-windows`** | Single TFM for the whole app; required because `Microsoft.Web.Administration` and the self-elevation flow are Windows-only. |
| **SQL via `sqlcmd` inside the container** | Avoids adding `Microsoft.Data.SqlClient`; the interface boundary makes it trivial to swap later. |
| **Centralised error handling** | `OperationRunner` catches per-action exceptions and reports them in the activity log; `App` shows anything escaping a click handler; `Program.cs` catches fatal errors. |
| **Admin enforcement** | `AdminElevation` relaunches the app elevated (UAC prompt) when it isn't. |
| **No hardcoded values** | Container name, SA password, port, GitHub APIs, IIS feature list, hostname suffix, base directory - all in `appsettings.json`. |

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK** - <https://dotnet.microsoft.com/download/dotnet/10.0>
- Docker Desktop (Linux containers)
- A user account that can elevate to Administrator (UAC prompt will appear)

## Build

All commands run from `DnnManager.NET\` (the folder containing `DnnManager.csproj`).

### Debug build (default, fast incremental)

```bash
cd DnnManager.NET
dotnet build
```

Output: `bin\Debug\net10.0-windows\dnnmgr.exe`.

### Release build

```bash
dotnet build -c Release
```

Output: `bin\Release\net10.0-windows\dnnmgr.exe`. Use this for distribution
or when measuring performance.

### Clean

```bash
dotnet clean
```

## Run

The app self-elevates: if launched non-elevated it triggers a UAC prompt and
re-launches itself elevated.

### Option A - `dotnet run` (recommended for development)

```bash
cd DnnManager.NET
dotnet run                # Debug
dotnet run -c Release     # Release
```

Accept the UAC prompt and the window opens. `dotnet run` returns immediately
(exit code 0) because the elevated instance is a separate process. From an
elevated terminal there's no prompt.

### Option B - run the built executable directly

```bash
.\bin\Release\net10.0-windows\dnnmgr.exe
```

Same self-elevation behaviour applies.

### Option C - publish as a single self-contained `.exe`

One file, no .NET runtime required on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

.\publish\dnnmgr.exe
```

The result is a single `dnnmgr.exe` (~70 MB) in `publish\`. Copy it anywhere -
it only needs `appsettings.json` next to it if you want to override defaults.

## Using the app

The sidebar holds every action:

| Page | What it does |
|---|---|
| **Projects** | Every project folder with its URL, IIS site state, SQL status, database and size. Open the site or folder, or remove the project. |
| **New project** | Download DNN into a new folder and create its IIS site and database. Typing the name of an existing folder offers to set that folder up instead. |
| **Existing folder** | Create the IIS site and/or local database for a folder that's already there. |
| **Clone project** | Copy a site (files + database) from a local folder or an FTP server. |
| **Live sites** | The FTP / SQL connections of your live websites (saved when cloning, or added with **+ Add**). Edit them (saved passwords load masked - use the eye to view them), and **Test connection** logs in to check they work. |
| **Prerequisites** | Check Docker and the IIS Windows features, and enable missing ones. |
| **Settings** (gear icon) | Edit `appsettings.json` - see [Configuration](#configuration). |

The **Activity** pane at the bottom shows each step as it runs. **Cancel** stops
the running operation, and **Copy** puts the log on the clipboard. The chevron
(or clicking **Activity**) hides the log down to its header bar, which keeps the
running operation, progress and Cancel in view. Click it again to bring the log back.
Every password field has an eye button to show or hide what's in it.

The sun / moon button next to **Projects folder** at the bottom of the sidebar switches between
the light and dark theme. The choice is saved as `DnnManager:Theme` in `appsettings.json`.
Until you pick one, the app follows the Windows app theme. Questions
(confirmations, production SQL credentials) open as dialogs. While an operation
runs, the pages stay browsable but nothing else can be started.

## Configuration

Settings live in the `appsettings.json` next to `dnnmgr.exe`. Edit them on the
**Settings** page (gear icon at the bottom of the sidebar), then **Save**. The
app reads them at startup, so it offers to restart and apply them. The page
edits everything except the IIS feature list and logging levels.
**Open appsettings.json** opens the file for those.

- `DnnManager:BaseDirectory` - where projects live (`C:\DNN` by default).
- `DnnManager:SitePort`, `DnnManager:HostnameSuffix`.
- `DnnManager:Theme` - `Light`, `Dark` or `System` (follow Windows). Set by the sidebar's theme button.
- `DnnManager:Docker:*` - container name, SA password, default port, suffixes.
  Keep these in sync with `docker-compose.yml`.
- `DnnManager:GitHubReleaseApis` - sources for DNN releases.
- `DnnManager:RequiredIisFeatures` - list checked & optionally enabled.

Environment variables prefixed with `DNNMGR_` override settings, e.g.
`DNNMGR_DnnManager__Docker__SaPassword=...`. The Settings page lists any
that are set, since they win over what it saves.

## Set up an existing project folder

For a DNN site whose files are **already** in a folder under `BaseDirectory`
(copied over by hand, checked out from git, left behind by an earlier run),
**Existing folder** creates only what is missing - the files
are never downloaded, copied or overwritten.

Flow (handled by [`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs)
→ [`HostExistingProjectUseCase`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs)):

1. **Pick the folder** - each one shows whether it already has an IIS site.
2. **Choose** `IIS website only`, `IIS website + local database` or
   `local database only`.
3. **Pick a backup** (when the database is included) - a `.bacpac` or `.bak`
   found in the project's `backups\` folder or its root (newest first), any
   file picked with **Browse…**, or none.
4. **IIS website** (skipped for database only) - checks the IIS features, then
   creates (or recreates) the site and app pool bound to
   `<folder>.<HostnameSuffix>`, grants the IIS identities access to the folder
   and starts the site.
5. **Database** (unless IIS only) - starts the shared SQL container. The
   database is the one `web.config` already uses on the local container, or
   otherwise `<folder>_dnndev`. Then:
   - **with a backup**, restores it (`.bacpac` via SqlPackage, `.bak` via
     `RESTORE`) - asking first if the database already exists - and points
     `dbo.PortalAlias` at the local hostname so the site answers there;
   - **without one**, keeps an existing database as it is, or creates it empty
     (run the install wizard, or restore later by running **Existing folder**
     again with **local database only** and a backup).

   Unless `web.config` already uses the local container, it then asks before
   pointing `web.config`'s `SiteSqlServer` at the database.

With the website, a database problem (e.g. Docker not running) is reported and
skipped; with **database only** it fails the run, since the database is the
whole job.

Typing the name of an existing folder into **New project** offers
the same three choices, plus downloading DNN over the folder as before.

## Clone existing project

The **Clone** action copies an existing DNN site (files + database) into a brand
new project under `BaseDirectory` with its own hostname, IIS site and DB.

Flow (handled by [`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs)
→ [`CloneProjectUseCase`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Pick source kind** - `Local folder` or `FTP server`.
2. **Pick source location**:
   - Local: a subdirectory of `BaseDirectory`.
   - FTP: a saved project (reusing its FTP + SQL connections, and choosing
     full clone / files only / database only), or a new one: enter
     host/port/user/password, **Connect & browse**, and double-click through
     the remote tree until the folder shown is the site root. The connection
     is saved for the project when the clone starts.
3. **Name the new project** - the hostname becomes `<name>.<HostnameSuffix>`.
4. **Backup the source DB** - automatic, no prompt. If the source's
   `SiteSqlServer` connection points at the local Docker container, the backup
   uses `BACKUP DATABASE ... TO DISK = '/var/opt/mssql/backup/<file>.bak'`
   inside the container and `docker cp`s the result to `%TEMP%`. Otherwise it
   runs against the remote SQL Server via `Microsoft.Data.SqlClient`.
5. **Copy site files** into the new project directory.
6. **Create the new login + DB** and **restore** the backup with `WITH REPLACE,
   MOVE` and a logical-file remap.
7. **Remap the database user** so the new login owns the restored DB (the
   source's user mapping is overwritten by `RESTORE`).
8. **Rewrite `dbo.PortalAlias`** so portal 0's primary alias becomes the new
   hostname (see *Notes on cloning* below).
9. **Patch `web.config`** with the new `SiteSqlServer` connection (supports the
   `configSource="..."` pattern; the external file is what gets rewritten).
10. **Create the IIS site** and host entry.

FTP profiles are stored per-user under
`%LocalAppData%\dnnmgr\ftp-profiles.json`. Passwords are protected with the
Windows DPAPI (`CurrentUser` scope) - not portable to other accounts.

### Notes on cloning

- DNN's user-facing **"Connection To The Database Failed"** page is shown for
  *any* startup exception, not just DB connection issues. When investigating,
  always read the actual exception from
  `Portals\_default\Logs\<date>.log.resources` inside the project.
- The portal-alias step (handled by
  [`ISqlServerService.RemapPortalAliasesAsync`](src/DnnManager.Application/Abstractions/Interfaces.cs))
  rewrites the first `*.<HostnameSuffix>` alias for `PortalID = 0` into the new
  hostname, inserts one if none matched, and removes leftover stale aliases.
  Without this step the cloned site throws
  `NullReferenceException at PortalSettingsController.ConfigureActiveTab`
  because no alias matches the incoming request.

## Extending

- **New page**: add a `UserControl` under `Pages/` (Presentation) that runs its
  `UseCase` (Application, registered with DI) through `OperationRunner`, then add
  a sidebar entry in `MainWindow.xaml` and its type to the `Pages` map in
  `MainWindow.xaml.cs`.
- **Swap SQL driver**: implement `ISqlServerService` with `Microsoft.Data.SqlClient`
  and register it instead of `SqlServerService`.
- **Add tests**: every use case takes pure interfaces - drop in fakes / mocks
  (no test project shipped here to keep the scope focused).

## Component map

| Area | C# location |
|---|---|
| Main window / navigation | [`MainWindow.xaml`](src/DnnManager.Presentation/MainWindow.xaml) |
| Setup | [`UseCases/SetupProjectUseCase.cs`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs) + [`SetupPage`](src/DnnManager.Presentation/Pages/SetupPage.xaml.cs) |
| Existing folder (IIS / DB only) | [`UseCases/HostExistingProjectUseCase.cs`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) + [`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |
| Remove | [`UseCases/RemoveProjectUseCase.cs`](src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs) + [`ProjectsPage`](src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs) |
| Check prerequisites | [`UseCases/CheckPrerequisitesUseCase.cs`](src/DnnManager.Application/UseCases/CheckPrerequisitesUseCase.cs) |
| Show projects info | [`UseCases/ListProjectsUseCase.cs`](src/DnnManager.Application/UseCases/ListProjectsUseCase.cs) |
| Clone project | [`UseCases/CloneProjectUseCase.cs`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) + [`ClonePage`](src/DnnManager.Presentation/Pages/ClonePage.xaml.cs) |
| Settings | [`SettingsPage`](src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs) + [`Files/AppSettingsFile.cs`](src/DnnManager.Infrastructure/Files/AppSettingsFile.cs) |
| FTP browse / credentials | [`Files/FtpBrowser.cs`](src/DnnManager.Infrastructure/Files/FtpBrowser.cs), [`Files/FtpProfileStore.cs`](src/DnnManager.Infrastructure/Files/FtpProfileStore.cs) |
| GitHub release lookup | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| Docker / sqlcmd | [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs), [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Shared SQL container | [`docker-compose.yml`](docker-compose.yml) (ships next to the app), brought up via [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs) (`ComposeUpAsync`) |

## Notes / limitations

- Logical-file remap on RESTORE for local backups is implemented; for remote
  imports we recommend `WITH MOVE` discovery via a parallel `IRemoteSqlService`.
