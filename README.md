# DnnManager.NET

A production-ready DNN management tool, with a **Clean Architecture** solution
and a **custom arrow-key terminal UI** built using only `System.Console` primitives.

## Architecture

The solution is built around clear boundaries that separate UI, orchestration, IIS,
Docker, GitHub, SQL Server, file I/O and state into maintainable layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      DnnManager.Presentation                        │
│  Custom TUI (Console.ReadKey + SetCursorPosition + colours), DI,    │
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
├── DnnManager.csproj            ← single project (net10.0-windows, Exe)
├── app.manifest                 ← requireAdministrator
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
        ├── Program.cs           ← composition root (Host + DI + config + logging)
        ├── AdminElevation.cs    ← refuses to run unless elevated
        ├── Tui/                 ← ConsoleScreen, SelectableList, ConfirmDialog,
        │                         TextPrompt, StatusWriter, Theme, Tui adapters
        └── Views/               ← one View per menu item
```

The Clean Architecture **dependency rule still holds** at the namespace level
(`DnnManager.Presentation` → `DnnManager.Application` → `DnnManager.Domain`,
`DnnManager.Infrastructure` → `DnnManager.Application` → `DnnManager.Domain`),
it's just no longer enforced by project boundaries. Keep new code in the
correct `src/<layer>` folder.

## Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; UI can be swapped (e.g. WPF) without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `IDockerService`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `IHttpConnectivityChecker`, `IUserPrompt`, `IProgressReporter`. Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; we still log and surface unexpected exceptions centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI, configuration binding (`appsettings.json` + `DNNMGR_*` env vars), structured logging via `Microsoft.Extensions.Logging`. |
| **Custom TUI** | `ConsoleScreen` wraps `Console.SetCursorPosition` / `ForegroundColor` / `Clear`. `SelectableList<T>` and `ConfirmDialog` handle `ConsoleKey.UpArrow/DownArrow/LeftArrow/RightArrow/Enter/Escape` and 1-9 quick-select. |
| **Adapter pattern for TUI → app layer** | `TuiProgressReporter` and `TuiUserPrompt` implement application interfaces so use cases never know they're driven from a console. |
| **`net10.0-windows`** | Single TFM for the whole app; required because `Microsoft.Web.Administration` and the self-elevation flow are Windows-only. |
| **SQL via `sqlcmd` inside the container** | Avoids adding `Microsoft.Data.SqlClient`; the interface boundary makes it trivial to swap later. |
| **Centralised error handling** | `MainMenuView.RunAsync` catches per-action exceptions, logs them, and returns to the menu; `Program.cs` catches fatal errors. |
| **Admin enforcement** | `app.manifest` requests elevation; `AdminElevation` double-checks. |
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
re-launches itself in a new elevated console window.

### Option A - `dotnet run` (recommended for development)

```bash
cd DnnManager.NET
dotnet run                # Debug
dotnet run -c Release     # Release
```

You'll see `Elevation required - relaunching as Administrator…`, accept the UAC
prompt, and the menu appears in a new console window. The original `dotnet run`
shell exits immediately (exit code 0) because the elevated process is its own
new console.

> **Tip:** If you'd rather keep everything in the *same* console, open an
> elevated terminal first and then run the command above - the elevation
> check passes and no new window is spawned.

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

## TUI controls

| Key | Action |
|---|---|
| `↑` / `↓` | Move selection in any list/menu |
| `1`–`9`   | Quick-select that item |
| `Home` / `End` | Jump to first / last item |
| `Enter`   | Confirm selection |
| `Esc`     | Cancel / back |
| `←` / `→` | Switch Yes/No in confirm dialogs |
| `Y` / `N` | Direct Yes/No answers |
| `Ctrl+C`  | Graceful cancel |

## Configuration

`appsettings.json` (at the project root) - adjust without recompiling:

- `DnnManager:BaseDirectory` - where projects live (`C:\DNN` by default).
- `DnnManager:SitePort`, `DnnManager:HostnameSuffix`.
- `DnnManager:Console:WindowWidth` / `WindowHeight` - initial console size set on
  startup (defaults 100 x 30). Ignored when the terminal doesn't support resize.
- `DnnManager:Docker:*` - container name, SA password, default port, suffixes.
- `DnnManager:GitHubReleaseApis` - sources for DNN releases.
- `DnnManager:RequiredIisFeatures` - list checked & optionally enabled.

Environment variables prefixed with `DNNMGR_` override settings, e.g.
`DNNMGR_DnnManager__Docker__SaPassword=...`.

## Set up an existing project folder

For a DNN site whose files are **already** in a folder under `BaseDirectory`
(copied over by hand, checked out from git, left behind by an earlier run),
**Setup an existing project folder** creates only what is missing - the files
are never downloaded, copied or overwritten.

Flow (handled by [`ExistingProjectView`](src/DnnManager.Presentation/Views/ExistingProjectView.cs)
→ [`HostExistingProjectUseCase`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs)):

1. **Pick the folder** - each one shows whether it already has an IIS site.
2. **Choose** `IIS website only`, `IIS website + local database` or
   `local database only`.
3. **Pick a backup** (when the database is included) - a `.bacpac` or `.bak`
   found in the project's `backups\` folder or its root (newest first), one at
   a path you type, or none.
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
     (run the install wizard, or restore later with **Database → Overwrite
     database**).

   Unless `web.config` already uses the local container, it then asks before
   pointing `web.config`'s `SiteSqlServer` at the database.

With the website, a database problem (e.g. Docker not running) is reported and
skipped; with **database only** it fails the run, since the database is the
whole job.

Typing the name of an existing folder into **Setup a new DNN project** offers
the same three choices, plus downloading DNN over the folder as before.

## Clone existing project

The **Clone** action copies an existing DNN site (files + database) into a brand
new project under `BaseDirectory` with its own hostname, IIS site and DB.

Flow (handled by [`CloneView`](src/DnnManager.Presentation/Views/CloneView.cs)
→ [`CloneProjectUseCase`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Pick source kind** - `Local folder` or `FTP server`.
2. **Pick source location**:
   - Local: a `SelectableList` of subdirectories under `BaseDirectory`.
   - FTP: pick a saved profile or enter host/port/user/password once; after a
     successful connect you're offered to save the credentials. Then navigate
     the remote tree with `[ ✓ Clone THIS folder ]` / `[ ↩ Go up ]` / subdir
     entries until you reach the site root.
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

Pressing `Esc` at any step silently returns to the main menu - no "cancelled"
message, no pause.

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

- **New menu item**: add a `View` (Presentation), a `UseCase` (Application),
  register both with DI, append it to the `items` array in `MainMenuView`.
- **Swap SQL driver**: implement `ISqlServerService` with `Microsoft.Data.SqlClient`
  and register it instead of `SqlServerService`.
- **Add tests**: every use case takes pure interfaces - drop in fakes / mocks
  (no test project shipped here to keep the scope focused).

## Component map

| Area | C# location |
|---|---|
| Menu | [`Views/MainMenuView.cs`](src/DnnManager.Presentation/Views/MainMenuView.cs) |
| Setup | [`UseCases/SetupProjectUseCase.cs`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs) + [`SetupView`](src/DnnManager.Presentation/Views/SetupView.cs) |
| Existing folder (IIS / DB only) | [`UseCases/HostExistingProjectUseCase.cs`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) + [`ExistingProjectView`](src/DnnManager.Presentation/Views/ExistingProjectView.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |
| Remove | [`UseCases/RemoveProjectUseCase.cs`](src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs) + [`RemoveView`](src/DnnManager.Presentation/Views/OtherViews.cs) |
| Check prerequisites | [`UseCases/CheckPrerequisitesUseCase.cs`](src/DnnManager.Application/UseCases/CheckPrerequisitesUseCase.cs) |
| Show projects info | [`UseCases/ListProjectsUseCase.cs`](src/DnnManager.Application/UseCases/ListProjectsUseCase.cs) |
| Export / Import DB  | [`UseCases/DatabaseUseCases.cs`](src/DnnManager.Application/UseCases/DatabaseUseCases.cs) |
| Clone project | [`UseCases/CloneProjectUseCase.cs`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) + [`CloneView`](src/DnnManager.Presentation/Views/CloneView.cs) |
| FTP browse / credentials | [`Files/FtpBrowser.cs`](src/DnnManager.Infrastructure/Files/FtpBrowser.cs), [`Files/FtpProfileStore.cs`](src/DnnManager.Infrastructure/Files/FtpProfileStore.cs) |
| GitHub release lookup | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| Docker / sqlcmd | [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs), [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Shared SQL container | [`docker-compose.yml`](docker-compose.yml) (ships next to the app), brought up via [`Docker/DockerService.cs`](src/DnnManager.Infrastructure/Docker/DockerService.cs) (`ComposeUpAsync`) |

## Notes / limitations

- The **remote production** export/import branches are scaffolded
  (`ExportDatabaseUseCase` / `ImportDatabaseUseCase`) but only the developer path
  (local Docker SQL Server) is fully implemented. The clean boundaries make
  adding the remote flow a matter of extending `ISqlServerService` (e.g. a
  `BackupOnRemoteAsync(...)`) without touching the Presentation.
- Logical-file remap on RESTORE for local backups is implemented; for remote
  imports we recommend `WITH MOVE` discovery via a parallel `IRemoteSqlService`.
