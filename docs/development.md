# Development

Building, running and testing DNN Manager from source, publishing it, building
the installer, and how the code is organised. New here? Start with the
[README](../README.md).

**Contents**

- [Prerequisites](#prerequisites)
- [Build](#build)
- [Tests](#tests)
- [Run](#run)
- [Architecture](#architecture)
- [Extending](#extending)
- [Component map](#component-map)

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK** to build - <https://dotnet.microsoft.com/download/dotnet/10.0>
- Docker Desktop (Linux containers) for the shared SQL Server - set it up once
  with **Set up docker-compose** in **Settings → Docker container** (or point
  **Settings → Database server** at a SQL Server you already run)
- A user account that can elevate to Administrator (UAC prompt will appear)

## Build

All commands run from `DnnManager.NET\` (the folder containing `DnnManager.csproj`).

```bash
dotnet build                # Debug   -> bin\Debug\net10.0-windows\DnnManager.exe
dotnet build -c Release     # Release -> bin\Release\net10.0-windows\DnnManager.exe
dotnet clean
```

## Tests

`tests\DnnManager.IntegrationTests` (MSTest) holds the tests - not part of the
app:

```bash
dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration"   # fast - no IIS, no SQL Server
dotnet test tests\DnnManager.IntegrationTests                                        # everything, about 15 minutes
```

The fast ones check the install's parts (DNN's install output, the template,
connection strings, password rules and hashing, the Credential Manager, the
settings) and that New project refuses a bad host password, a folder that is
too deep or a database it can't reach before it creates anything.

The integration tests (`TestCategory=Integration`) run DNN Manager's own
`SetupProjectUseCase` on a clean DNN 10.3.3 install package, with **IIS
Express** playing IIS (no administrator rights needed), and check what a
visitor sees: the home page instead of the wizard, the host signing in (a wrong
password refused), the portal and its alias, DNN's tables at the files'
version, a restart that doesn't install again, no errors in DNN's log, no
password in any message or file, and **Change host password** - once for each
kind of database (LocalDB with Windows authentication, the local SQL container,
a SQL login of its own on an empty database, a LocalDB file) -, and that
**Manual DNN setup** leaves DNN's wizard for the first visit. They need IIS
Express, SQL Server Express LocalDB and Docker Desktop; a test whose
prerequisite is missing is *inconclusive*, not failed. The package is
downloaded once into `%LOCALAPPDATA%\DnnManagerTests\cache` (or set
`DNNMANAGER_TEST_DNN_ZIP` to one you have).

What they make is their own, and removed afterwards: a folder under
`%LOCALAPPDATA%\DnnManagerTests`, a LocalDB instance `dnnit_<id>` and a SQL
Server container `dnnit-mssql-<id>`. They never touch your projects, settings,
`MSSQLLocalDB` instance or SQL Server container.

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
.\bin\Release\net10.0-windows\DnnManager.exe
```

### Option C - publish a single self-contained `.exe`

One file, no .NET runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PortableExe=true -o publish

.\publish\DnnManager-2.3.0-x64.exe
```

`-p:PortableExe=true` names the exe `DnnManager-<version>-x64.exe` (without it,
it's `DnnManager.exe`). The publish output holds only the program. Settings live in
`Documents\DnnManager` and are created on first start (see
[Configuration](user-guide.md#configuration)).

> **"Access to the path '...\publish\DnnManager.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

### Build the installer

```powershell
.\src\DnnManager.Installer\build.ps1
```

Publishes the app (self-contained, single file) into
`src\DnnManager.Installer\bin\app`, then compiles
[`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss) with Inno Setup
into `publish\DnnManagerSetup-<version>-x64.exe`. The version
comes from `<Version>` in `DnnManager.csproj`. It uses an installed Inno Setup 6
when there is one, otherwise it downloads a pinned copy (the `Tools.InnoSetup`
package from nuget.org) into `src\DnnManager.Installer\bin\tools` - no admin
rights needed. Everything made along the way (the published app, wizard images,
Inno Setup) is in `src\DnnManager.Installer\bin`; the finished Setup is in
`publish\`.
`-SkipPublish` reuses the last publish; `-Iscc <path>` picks the compiler.

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

VS Code tasks for build, publish and the installer are in `.vscode/tasks.json`.

## Architecture

The solution separates UI, orchestration, IIS, Docker, GitHub, SQL Server, file
I/O and state into layers:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      DnnManager.Presentation                        │
│  WPF GUI: sidebar pages, activity log, dialogs, themes, settings.   │
│  Composition root (settings + Host + DI), admin elevation.          │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ depends on interfaces only
┌──────────────────────────▼──────────────────────────────────────────┐
│                       DnnManager.Application                        │
│  Use cases: Setup / Import / Export / HostExisting / Clone /        │
│  Remove / ControlSites / IisServer / SetupSqlContainer.             │
│  Abstractions (interfaces for IIS, SQL, Releases, Files…).          │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ implements interfaces
┌──────────────────────────▼──────────────────────────────────────────┐
│                     DnnManager.Infrastructure                       │
│  IIS (Microsoft.Web.Administration), Docker CLI, GitHub releases,   │
│  SQL (sqlcmd in the container, SqlClient + SqlPackage for remote),  │
│  web.config, settings.json (load, migrate, save), log files.        │
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
compiled into a single assembly (`DnnManager.exe`).

```
DnnManager.NET/
├── DnnManager.csproj            ← single project (net10.0-windows, WPF WinExe)
├── app.manifest                 ← asInvoker; AdminElevation relaunches elevated
├── tests/
│   └── DnnManager.IntegrationTests/  ← MSTest: the automatic DNN setup end to end (IIS Express, LocalDB, Docker) - see Tests
└── src/
    ├── DnnManager.Domain/
    │   ├── Models.cs            ← DnnProject, DnnRelease, DatabaseConfig, …
    │   ├── ProjectName.cs       ← project name validation
    │   └── Result.cs            ← Result / Result<T> (no exceptions across layers)
    ├── DnnManager.Application/
    │   ├── Abstractions/        ← all interfaces consumed by use cases
    │   ├── Configuration/       ← UserSettings (settings.json, defaults, validation), AppOptions
    │   ├── UseCases/            ← one class per top-level action
    │   └── DependencyInjection.cs
    ├── DnnManager.Infrastructure/
    │   ├── Iis/                 ← IIS via Microsoft.Web.Administration
    │   ├── Docker/              ← docker-compose.yml for the shared SQL container, from the settings, and running it
    │   ├── Sql/                 ← sqlcmd in the container, remote backup, SqlPackage, connection test; DatabaseProvisioner (Test connection, create, the site's login), LocalDB files, connection strings
    │   ├── Dnn/                 ← DNN's unattended install (Install.aspx), its template and output, the host password's hash
    │   ├── Github/              ← GitHub API + DNN package downloader
    │   ├── Settings/            ← AppDataPaths (Documents\DnnManager), SettingsStore, SettingsMigrations, WindowsCredentialStore
    │   ├── Files/               ← file copy, site .zip import / export, daily log file
    │   ├── Projects/            ← file-system project repository; ProjectRecords (how each project was installed)
    │   ├── Prereq/              ← IIS feature checks
    │   ├── WebConfigs/          ← web.config SiteSqlServer read / write
    │   ├── Processes/           ← shared ProcessRunner; the app's own power throttling (EcoQoS)
    │   ├── Terminal/            ← a shell in a Windows pseudo console (ConPTY)
    │   ├── Startup/             ← the "start at sign-in" scheduled task
    │   ├── Monitoring/          ← ServerStateMonitor: the live state of the projects, IIS and this PC - what tells it to look (ChangeSources) and what it measures with
    │   ├── SiteLogs/            ← a site's logs (DNN, IIS, event logs) and following one as it is written
    │   └── DependencyInjection.cs
    ├── DnnManager.Installer/    ← not compiled into the app
    │   ├── DnnManager.iss       ← Inno Setup script (per-user install, shortcuts, uninstall)
    │   ├── build.ps1            ← publish + compile the installer
    │   └── bin/                 ← build files (published app, wizard images, Inno Setup) - not in git
    └── DnnManager.Presentation/
        ├── Program.cs           ← composition root (settings + Host + DI), starts WPF
        ├── AdminElevation.cs    ← relaunches elevated when needed
        ├── RunningMarker.cs     ← named mutex while the app runs - the installer checks it before replacing the app
        ├── SingleInstance.cs    ← a second start hands over to the running app, which shows its window
        ├── App.xaml             ← merges the palette, tokens and control styles; the sidebar's own styles
        ├── MainWindow.xaml      ← sidebar navigation + page host + activity log + status bar
        ├── Pages/               ← one page per sidebar item (incl. Settings)
        │   └── Projects/        ← the Projects table's row, columns and right-click menu
        ├── Assets/              ← dnn.ico - the exe and window icon (DNN logo mark)
        ├── Controls/            ← StatusBar, IisStatus, TerminalPanel (Output / Logs / Terminal), OutputView, LogView, LogsView, PanelSearch, ToastView, InputDialog, MessageDialog, ExistingFolderOptions, PasswordInput, DatabaseCheckList, HostPasswordDialog
        ├── Terminal/            ← the terminal itself: screen buffer + VT parser, the view that draws it, the shell session
        ├── Themes/              ← LightTheme / DarkTheme colour palettes, Tokens (radii, heights, padding)
        │   └── Controls/        ← the reusable control styles, one dictionary per kind (see Control styles)
        └── Services/            ← ActivityLog, OperationRunner, ServerStore, EfficiencyMode, LiveSettings, DnnReleaseCatalog, TerminalService, ThemeManager, Toast, IdeLocator, SsmsConnectDialog, SettingsStartup, GUI adapters
```

### Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; the UI was swapped from a terminal UI to WPF without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `ISqlConnectionTester`, `IUserPrompt`, `IProgressReporter`, … Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; unexpected exceptions are still logged and surfaced centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI and logging via `Microsoft.Extensions.Logging`. `AppOptions` is made from `settings.json` at startup, with `DNNMANAGER_*` env vars on top. There is one instance, shared: saving on the Settings page puts the new values into it (`LiveSettings`), so they apply without a restart; what caches something made from a setting follows its `Changed` event. |
| **Program and user data apart** | The installer owns the install folder; the app owns `Documents\DnnManager`. `settings.json` is versioned: `SettingsStore` backs it up and runs `SettingsMigrations` when its format is older, and fills in new keys from `UserSettings`' defaults. |
| **WPF, code-behind pages** | One `UserControl` per sidebar item, made on its first visit and kept, so its lists load once (Settings is made anew each time). |
| **Live state instead of Refresh** | One monitor reads the system and one store holds what the window shows. Windows' own notifications say when to look; timers cover what has none. See [Live updates](#live-updates). |
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs, and backs the status bar's **Cancel** button. A cancelled operation is undone through the scope's [`OperationUndo`](../src/DnnManager.Application/UseCases/OperationUndo.cs): each step notes how to take back what it is about to make, before it starts. |
| **Adapters for GUI → app layer** | `GuiProgressReporter` (writes to the activity log) and `GuiUserPrompt` (modal dialogs) implement application interfaces, so use cases never know what drives them. |
| **Runtime theming** | Colours live in `LightTheme` / `DarkTheme`; everything references them with `DynamicResource`, and `ThemeManager` swaps the dictionary (and the title bar's dark mode) live. |
| **Reusable control styles** | Every control's look is a style in `Themes/Controls`, not set per page: a plain `<TextBox />` or `<Button />` is already styled, and sizes come from `Tokens.xaml`, so all controls stay alike. See [Control styles](#control-styles). |
| **SQL** | The local container is checked by logging in with `Microsoft.Data.SqlClient` and driven with `sqlcmd` via `docker exec`; remote / Azure SQL uses `Microsoft.Data.SqlClient` and SqlPackage (`.bacpac`). |
| **Centralised error handling** | `OperationRunner` catches per-action exceptions and reports them in the activity log; `App` shows anything escaping a click handler; `Program.cs` catches fatal errors. |
| **Admin enforcement** | `AdminElevation` relaunches the app elevated (UAC prompt) when it isn't. |
| **No hardcoded values** | Container name, SA password, port, GitHub APIs, IIS feature list, hostname suffix, base directory, theme - all in `settings.json`. |

### Live updates

The Projects table, the IIS indicator and the status bar's figures show one
shared state that keeps itself current, the way Docker Desktop follows its
engine: take a snapshot, follow the changes as they happen, and reconcile now
and then, because a notification can be missed.

- [`ServerStateMonitor`](../src/DnnManager.Infrastructure/Monitoring/ServerStateMonitor.cs)
  (background threads) is the only thing that reads the projects, IIS and this
  PC's figures. It keeps what it last read and raises one event with what
  differs: a project added, removed or changed - and which part of it: site,
  worker figures, database, size… -, IIS's state, the PC's figures, the
  connection.
- [`ServerStore`](../src/DnnManager.Presentation/Services/ServerStore.cs) (UI
  thread) applies that to the row objects the table is bound to. A row is never
  made again while its project exists, and only the properties of the changed
  part are announced - so one site stopping redraws that row's state, and the
  check boxes, search, sorting, scroll position and open details are not
  touched. It also runs the rows' actions: the row says *Starting…* at once,
  the use case runs, the sites are read again and the row shows what IIS
  reports - the new state, or the old one and a toast when it failed.
- Both are in one process, so the "connection" between them is a .NET event
  handed to the UI thread - no IPC, no WebSocket, no timer in the UI.

Windows tells the monitor when to look
([`ChangeSources.cs`](../src/DnnManager.Infrastructure/Monitoring/ChangeSources.cs));
what Windows has no notification for is read on a timer, each at its own pace:

| What | How it is noticed |
|---|---|
| IIS started or stopped | Pushed: the service control manager reports the web service's status. |
| A site or app pool added, removed, started or stopped | Pushed: `applicationHost.config` being written, and what IIS writes to the System event log. Reconciled every 5 s (30 s while Projects isn't on screen) - IIS has no notification for a site's running state. |
| A worker process started or ended | The set of `w3wp` processes, every 2 s ¹ - a change reads the sites again. |
| A folder in the projects folder made, removed or renamed | Pushed: the projects folder is watched - the sites' folders are read again (DNN version, database). |
| What a site's folder holds (DNN version, web.config database) | When the site turns up or serves another folder, and every 30 s ¹. |
| Worker-process CPU, memory and disk I/O | Every 2 s ¹. |
| HTTP traffic per site | Every 5 s ¹, and only while that column is shown. |
| The SQL Server and its databases | Every 10 s ¹. |
| Each project's database (`web.config`) and DNN version | Every 30 s ¹, and after an operation. |
| Folder sizes | Every 10 min ¹, and after an operation (one that ends while the window is minimized: once it is restored ²). |
| This PC's memory and CPU; its disk | Every 2 s; every 10 s - not while the window is minimized ². |
| The PC woke up | Pushed (power event) - or a timer tick that comes half a minute late. Everything is read again. |

¹ Only while the Projects page is on screen and the window isn't minimized;
showing it again reads everything once, at once.
² With **Save resources while the window can't be seen** on (the default) - see
[Efficiency mode](user-guide.md#efficiency-mode-while-the-window-cant-be-seen).

A notification only says "look again" - the monitor then reads the real state,
so a missed or doubled one does no harm. The intervals are counted from when a
read was last asked for or finished, on a clock that doesn't follow the PC's
date and time. What can't be read (IIS's configuration, the projects folder)
is kept as it was and shown as *Reconnecting…*; it is tried again every 5 s,
and a source that stopped notifying is attached again the same way. An IIS
that doesn't answer at all isn't waited for longer than 15 s. The only loading
state is the first snapshot.

### Control styles

The app's controls are styled in one place, so every page looks the same and a
new page needs no styling of its own. [`App.xaml`](../src/DnnManager.Presentation/App.xaml)
merges, in order: the colour palette (`LightTheme` / `DarkTheme`),
[`Tokens.xaml`](../src/DnnManager.Presentation/Themes/Tokens.xaml) and the control
dictionaries in [`Themes/Controls/`](../src/DnnManager.Presentation/Themes/Controls/).

| Dictionary | Default look for | Keyed variants (`Style="{StaticResource …}"`) |
|---|---|---|
| `ButtonStyles` | `Button` | `Primary`, `Danger`, `IconButton`, `IconToggleButton`, `LinkButton`, `ExpandToggle` |
| `InputStyles` | `TextBox`, `PasswordBox`, `ComboBox` (select) | `SearchBox` (magnifier, `Tag` as placeholder, ✕ to clear) |
| `SelectionStyles` | `CheckBox`, `RadioButton` | `ToggleSwitch`, `TableCheckBox` |
| `MenuStyles` | `ToolTip`, `ContextMenu`, `MenuItem`, menu separators | - |
| `ListStyles` | `ListBox`, `ScrollBar`, `DataGrid` | - |
| `LayoutStyles` | - | `PageTitle`, `PageSubtitle`, `CardTitle`, `FieldLabel`, `Hint`, `ErrorLine`, `Card`, `InfoBanner`, `WarnBanner` |

`Tokens.xaml` holds what the styles share: `ControlRadius` (4 - buttons,
inputs, selects, menus), `SmallRadius` (3 - check boxes), `CardRadius` (6),
`ControlHeight` (30 - buttons, inputs and selects line up side by side) and
`InputPadding`. Change a token and every control using it follows. Colours are
never set in a style directly - always a palette key with `DynamicResource`, so
the theme switch repaints them.

## Extending

- **New control style**: put it in the matching dictionary in
  `Themes/Controls/` - without `x:Key` to style every control of that type, or
  with one for a variant - and take sizes from `Tokens.xaml`. For a new kind of
  control, add a dictionary (a `.xaml` with `x:Class` and its partial class in
  `ControlDictionaries.cs`) and merge it in `App.xaml` after `Tokens`.
- **New page**: add a `UserControl` under `Pages/` (Presentation) that runs its
  use case (Application, registered with DI) through `OperationRunner`, then add
  a sidebar entry in `MainWindow.xaml` and its type to the `Pages` map in
  `MainWindow.xaml.cs`. Reference colours with `DynamicResource` so the page
  follows the theme.
- **New colour**: add the same key to both `Themes/LightTheme.xaml` and
  `Themes/DarkTheme.xaml`.
- **New setting**: add the property, with its default, to a section of
  [`UserSettings`](../src/DnnManager.Application/Configuration/UserSettings.cs)
  (and a check to `Validate` if it needs one), then carry it into `AppOptions` in
  `ToAppOptions`. Existing files get it with its default on the next start - no
  migration needed.
- **Changing the settings format** (renaming, moving or re-meaning a key): raise
  `UserSettings.CurrentVersion` and add an `ISettingsMigration` from the
  previous version to
  [`SettingsMigrations`](../src/DnnManager.Infrastructure/Settings/SettingsMigrations.cs).
  The store backs the file up and runs the chain on the next start.
- **Installer**: files, shortcuts and Setup options are in
  [`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss). Code signing can be
  added there (`SignTool`) and in `build.ps1`.
- **Add tests**: in `tests\DnnManager.IntegrationTests` - every use case takes
  pure interfaces, and the stand-ins are in its `Support\` folder
  (`UntouchedIis`, `TestPrompt` - which never says yes -, `RecordingReporter`,
  IIS Express as `IIisManager`…). A test that needs IIS Express, LocalDB or
  Docker gets `[TestCategory("Integration")]` and is inconclusive without them.

## Component map

| Area | C# location |
|---|---|
| Main window / navigation / activity log | [`MainWindow.xaml`](../src/DnnManager.Presentation/MainWindow.xaml) |
| Status bar (IIS, resources, running operation, version) | [`Controls/StatusBar.xaml`](../src/DnnManager.Presentation/Controls/StatusBar.xaml.cs), [`Controls/IisStatus.xaml`](../src/DnnManager.Presentation/Controls/IisStatus.xaml.cs), [`UseCases/IisServerUseCase.cs`](../src/DnnManager.Application/UseCases/IisServerUseCase.cs), [`Services/ServerStore.cs`](../src/DnnManager.Presentation/Services/ServerStore.cs), [`Monitoring/HostResourceMonitor.cs`](../src/DnnManager.Infrastructure/Monitoring/HostResourceMonitor.cs) |
| Live state of the projects, IIS and this PC (no Refresh) | [`Monitoring/ServerStateMonitor.cs`](../src/DnnManager.Infrastructure/Monitoring/ServerStateMonitor.cs), [`Monitoring/ChangeSources.cs`](../src/DnnManager.Infrastructure/Monitoring/ChangeSources.cs), [`Monitoring/MonitorModel.cs`](../src/DnnManager.Infrastructure/Monitoring/MonitorModel.cs), [`Services/ServerStore.cs`](../src/DnnManager.Presentation/Services/ServerStore.cs) |
| Projects table, site overview (portals), start / stop / restart, remove, export | [`ProjectsPage`](../src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs), [`Pages/Projects/ProjectView`](../src/DnnManager.Presentation/Pages/Projects/ProjectView.xaml.cs), [`Pages/Projects/`](../src/DnnManager.Presentation/Pages/Projects/), [`Services/ServerStore.cs`](../src/DnnManager.Presentation/Services/ServerStore.cs), [`Monitoring/ProcessSampler.cs`](../src/DnnManager.Infrastructure/Monitoring/ProcessSampler.cs), [`UseCases/ControlSitesUseCase.cs`](../src/DnnManager.Application/UseCases/ControlSitesUseCase.cs), [`UseCases/RemoveProjectUseCase.cs`](../src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs), [`UseCases/ExportProjectUseCase.cs`](../src/DnnManager.Application/UseCases/ExportProjectUseCase.cs) |
| New project | [`SetupPage`](../src/DnnManager.Presentation/Pages/SetupPage.xaml.cs) + [`UseCases/SetupProjectUseCase.cs`](../src/DnnManager.Application/UseCases/SetupProjectUseCase.cs), [`UseCases/ImportProjectUseCase.cs`](../src/DnnManager.Application/UseCases/ImportProjectUseCase.cs) |
| Automatic DNN setup (DNN's install, its output, the host account and password) | [`Dnn/DnnInstaller.cs`](../src/DnnManager.Infrastructure/Dnn/DnnInstaller.cs), [`Dnn/DnnInstallTemplate.cs`](../src/DnnManager.Infrastructure/Dnn/DnnInstallTemplate.cs), [`Dnn/DnnInstallOutput.cs`](../src/DnnManager.Infrastructure/Dnn/DnnInstallOutput.cs), [`Dnn/MembershipPasswords.cs`](../src/DnnManager.Infrastructure/Dnn/MembershipPasswords.cs), [`Abstractions/DnnInstall.cs`](../src/DnnManager.Application/Abstractions/DnnInstall.cs), [`UseCases/ChangeHostPasswordUseCase.cs`](../src/DnnManager.Application/UseCases/ChangeHostPasswordUseCase.cs), [`Controls/HostPasswordDialog.xaml`](../src/DnnManager.Presentation/Controls/HostPasswordDialog.xaml.cs) |
| Databases: Test connection, create, the site's login, LocalDB files | [`Sql/DatabaseProvisioner.cs`](../src/DnnManager.Infrastructure/Sql/DatabaseProvisioner.cs), [`Sql/LocalDbFiles.cs`](../src/DnnManager.Infrastructure/Sql/LocalDbFiles.cs), [`Sql/ConnectionStrings.cs`](../src/DnnManager.Infrastructure/Sql/ConnectionStrings.cs), [`Abstractions/Databases.cs`](../src/DnnManager.Application/Abstractions/Databases.cs), [`Controls/DatabaseCheckList.xaml`](../src/DnnManager.Presentation/Controls/DatabaseCheckList.xaml.cs) |
| Passwords in the Windows Credential Manager; how each project was installed | [`Settings/WindowsCredentialStore.cs`](../src/DnnManager.Infrastructure/Settings/WindowsCredentialStore.cs), [`Projects/ProjectRecords.cs`](../src/DnnManager.Infrastructure/Projects/ProjectRecords.cs) |
| Tests | [`tests/DnnManager.IntegrationTests/`](../tests/DnnManager.IntegrationTests/) |
| Host project (IIS / DB) | [`ExistingFolderPage`](../src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs) + [`UseCases/HostExistingProjectUseCase.cs`](../src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](../src/DnnManager.Application/UseCases/Provisioning.cs) |
| Clone a project | [`ProjectMenu`](../src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs) (**Clone…**) + [`UseCases/CloneProjectUseCase.cs`](../src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) |
| SQL connection test | [`Sql/SqlConnectionTester.cs`](../src/DnnManager.Infrastructure/Sql/SqlConnectionTester.cs) |
| Projects right-click menu / IDE detection | [`Pages/Projects/ProjectMenu.cs`](../src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs), [`Services/IdeLocator.cs`](../src/DnnManager.Presentation/Services/IdeLocator.cs) |
| Test and set up (Docker, database server, IIS features) | [`DockerCard`](../src/DnnManager.Presentation/Controls/DockerCard.xaml.cs), [`DatabaseServerCard`](../src/DnnManager.Presentation/Controls/DatabaseServerCard.xaml.cs), [`IisCard`](../src/DnnManager.Presentation/Controls/IisCard.xaml.cs) + [`Prereq/WindowsPrerequisiteChecker.cs`](../src/DnnManager.Infrastructure/Prereq/WindowsPrerequisiteChecker.cs) (checks and enables the IIS features - **Set up IIS**) |
| Settings page (Save applies at once) | [`SettingsPage`](../src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs), [`Services/LiveSettings.cs`](../src/DnnManager.Presentation/Services/LiveSettings.cs), [`Configuration/AppOptions.cs`](../src/DnnManager.Application/Configuration/AppOptions.cs) |
| settings.json: format, defaults, validation | [`Configuration/UserSettings.cs`](../src/DnnManager.Application/Configuration/UserSettings.cs) |
| settings.json: load, save, backups, migrations | [`Settings/SettingsStore.cs`](../src/DnnManager.Infrastructure/Settings/SettingsStore.cs), [`Settings/SettingsMigrations.cs`](../src/DnnManager.Infrastructure/Settings/SettingsMigrations.cs), [`Settings/AppDataPaths.cs`](../src/DnnManager.Infrastructure/Settings/AppDataPaths.cs) |
| Settings error dialog at startup | [`Services/SettingsStartup.cs`](../src/DnnManager.Presentation/Services/SettingsStartup.cs) |
| Installer | [`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss), [`src/DnnManager.Installer/build.ps1`](../src/DnnManager.Installer/build.ps1) |
| Bottom panel (Output, Logs, Terminal; search) | [`Controls/TerminalPanel.xaml`](../src/DnnManager.Presentation/Controls/TerminalPanel.xaml.cs), [`Terminal/`](../src/DnnManager.Presentation/Terminal/) (`TerminalBuffer`, `TerminalView`, `TerminalSession`), [`Services/TerminalService.cs`](../src/DnnManager.Presentation/Services/TerminalService.cs), [`Terminal/PseudoConsole.cs`](../src/DnnManager.Infrastructure/Terminal/PseudoConsole.cs) |
| Keep warm (the flame) | [`KeepWarm/KeepWarmService.cs`](../src/DnnManager.Infrastructure/KeepWarm/KeepWarmService.cs), [`KeepWarm/KeepWarmRules.cs`](../src/DnnManager.Infrastructure/KeepWarm/KeepWarmRules.cs) (why sites go cold, the numbers), [`KeepWarm/KeepWarmPlan.cs`](../src/DnnManager.Infrastructure/KeepWarm/KeepWarmPlan.cs), [`KeepWarm/KeepWarmRequester.cs`](../src/DnnManager.Infrastructure/KeepWarm/KeepWarmRequester.cs), [`Projects/KeepWarmRecords.cs`](../src/DnnManager.Infrastructure/Projects/KeepWarmRecords.cs), [`Services/ServerStore.cs`](../src/DnnManager.Presentation/Services/ServerStore.cs) |
| Site tools: clear cache, the Logs tab | [`UseCases/ClearSiteCacheUseCase.cs`](../src/DnnManager.Application/UseCases/ClearSiteCacheUseCase.cs), [`SiteLogs/SiteLogs.cs`](../src/DnnManager.Infrastructure/SiteLogs/SiteLogs.cs), [`Controls/LogsView.xaml`](../src/DnnManager.Presentation/Controls/LogsView.xaml.cs), [`Controls/PanelSearch.cs`](../src/DnnManager.Presentation/Controls/PanelSearch.cs) |
| Start at sign-in | [`Startup/StartupTask.cs`](../src/DnnManager.Infrastructure/Startup/StartupTask.cs) |
| Efficiency mode while out of sight | [`Services/WindowOcclusion.cs`](../src/DnnManager.Presentation/Services/WindowOcclusion.cs), [`Services/EfficiencyMode.cs`](../src/DnnManager.Presentation/Services/EfficiencyMode.cs), [`Processes/PowerThrottling.cs`](../src/DnnManager.Infrastructure/Processes/PowerThrottling.cs) (EcoQoS) |
| Themes | [`Themes/`](../src/DnnManager.Presentation/Themes/), [`Services/ThemeManager.cs`](../src/DnnManager.Presentation/Services/ThemeManager.cs) |
| Control styles (buttons, inputs, selects, switches…) | [`Themes/Controls/`](../src/DnnManager.Presentation/Themes/Controls/), [`Themes/Tokens.xaml`](../src/DnnManager.Presentation/Themes/Tokens.xaml) |
| File copy, zip extract / create | [`Files/ProjectFileCopier.cs`](../src/DnnManager.Infrastructure/Files/ProjectFileCopier.cs) |
| GitHub release lookup (releases and pre-releases, the latest release by default) | [`Github/GitHubDnnReleaseService.cs`](../src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs), [`Services/DnnReleaseCatalog.cs`](../src/DnnManager.Presentation/Services/DnnReleaseCatalog.cs) |
| IIS helpers | [`Iis/IisManager.cs`](../src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| sqlcmd | [`Sql/SqlServerService.cs`](../src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Shared SQL container | `docker-compose.yml` made from the settings by [`Docker/DockerComposeService.cs`](../src/DnnManager.Infrastructure/Docker/DockerComposeService.cs) and run by [`UseCases/SetupSqlContainerUseCase.cs`](../src/DnnManager.Application/UseCases/SetupSqlContainerUseCase.cs) (Settings → Docker container → Set up docker-compose); the connection check is `LocalSqlContainer` in [`UseCases/Provisioning.cs`](../src/DnnManager.Application/UseCases/Provisioning.cs) |
