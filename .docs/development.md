# Development

Setting up, building, running and debugging DNN Manager from source, and the
conventions to follow. New here? Start with the [README](../README.md), then
[architecture.md](architecture.md).

| | |
|---|---|
| [architecture.md](architecture.md) | Layers, how an operation runs, live updates, project layout, design decisions, component map |
| [testing.md](testing.md) | The fast and the integration tests, adding a test |
| [releasing.md](releasing.md) | Version, changelog, portable exe, installer, GitHub release (built, tried and - once a reviewer approves - published by the release workflow), code signing (off until set up) |
| [release-notes/](release-notes/) | The notes of every GitHub release, one file per version |
| [configuration.md](configuration.md) | `Documents\DnnManager`, `dnnmanager.db`, every settings key, environment variables |
| [troubleshooting.md](troubleshooting.md) | Known problems and how to fix them |
| [security.md](security.md) | Administrator rights, secrets, what DNN Manager deletes, network exposure, open risks |
| [adr/](adr/) | Decisions that are costly to reverse: undo on failure, the layering rule, update trust, the elevation boundary |

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK `10.0.401`** to build - exactly that one: [`global.json`](../global.json)
  doesn't roll forward to a newer patch - <https://dotnet.microsoft.com/download/dotnet/10.0>
- For the installer only: Visual Studio's **C++ build tools** (*Desktop
  development with C++*) - the launcher is compiled with Native AOT; without
  them, `build.ps1 -NoLauncher` ([releasing.md](releasing.md#build-the-installer))
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

Fast tests: `dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration"` - see [testing.md](testing.md).

## Run

The app self-elevates: launched without Administrator rights it shows a UAC
prompt and relaunches itself elevated (managing IIS needs it). A build has no
launcher beside it, so it relaunches `DnnManager.exe` itself; only an installed
DNN Manager goes through `DnnManager-launcher.exe`.

### `dotnet run`

```bash
dotnet run                # Debug
dotnet run -c Release     # Release
```

Accept the UAC prompt and the window opens. `dotnet run` returns immediately
because the elevated instance is a separate process. From an elevated terminal
there's no prompt.

### The built executable

```bash
.\bin\Release\net10.0-windows\DnnManager.exe
```

## Debugging

DNN Manager needs Administrator rights (IIS), so the debugger has to have them
too: start Visual Studio / VS Code / Rider **as Administrator** and run from
there, or run the app and attach to `DnnManager.exe`. Started unelevated, the
app relaunches itself elevated - and that process isn't the one being debugged.

Where to look when something goes wrong:

- **`Documents\DnnManager\logs\dnnmanager-<yyyymmdd>.log`** ([configuration.md](configuration.md#where-your-files-are)) - every operation
  (Markdown: `# operation`, `## stage`, then its lines) and the app's own
  warnings and errors with their stack traces (`[warning]`, `[error]`,
  `[critical]`). Kept 30 days, 200 MB at most. Lines reach the file in
  batches, every 2 seconds - warnings and errors at once.
- **The Output tab** - the same operations, live; **Logs** - a site's DNN, IIS
  and Windows event logs.
- **Settings → About** - version, commit, .NET, Windows, IIS and Docker.
- **`Documents\DnnManager\dnnmanager.db`** - the settings, the workspace, the
  project records, keep warm and the saved DNN versions, in one SQLite file
  ([architecture.md](architecture.md#dnn-managers-database)). Close DNN Manager
  and open it with any SQLite browser to look at what it has saved - each
  setting is a row of `settings`, each value of the workspace a row of `state`
  ([configuration.md](configuration.md#looking-at-the-database)).
- An exception that escapes a click handler shows a message and is logged; one
  on a background thread ends the app - its stack trace is the last `[critical]`
  line in the log.

## Conventions

- **Files**: UTF-8 without BOM, CRLF line endings (see `.editorconfig`).
  PowerShell 5's `Set-Content` writes neither - use the editor, or
  `[IO.File]::WriteAllText` with `UTF8Encoding($false)`.
- **Threads**: the monitor raises its events on a background thread, under its
  lock - hand them on and return; `ServerStore` applies them on the UI thread.
  Pages never do file, registry, IIS or SQL work on the UI thread - `Task.Run`
  it, as the Details page and the log list do.
- **What the user's account can change is untrusted input** - DNN Manager runs
  elevated, and these are its choke points ([ADR 0004](adr/0004-elevation-boundary.md),
  [security.md](security.md#administrator-rights)):
  - **Settings and environment** that feed an administrator action (a folder it
    deletes in, an address it downloads from, a name that goes into SQL, a
    command line or PowerShell) get a rule in
    [`SettingRules`](../src/DnnManager.Application/Configuration/SettingRules.cs),
    called from `UserSettings.Validate` - which `DNNMANAGER_*` overrides go
    through too (`AppOptions.Problems`).
  - **Folders others can write** (a site's folder, the projects folder,
    Documents): paths compared only with `SafePath.IsSameOrInside` / `IsInside`
    (never `StartsWith`), joined with `SafePath.Under`, checked with `HasLink`
    before a write or delete, deleted with `SafePath.DeleteTree`; every zip
    unpacked entry by entry through `SafeZip.ExtractEntry` after
    `SafeZip.EnsureFits`. A refusal ends with `SafePath.LinkHint` (`Hint:`).
  - **A site's XML** is loaded with `SiteXml.Load` - never `XDocument.Load`,
    which reads a DTD.
- **SQL**: values as parameters; a name or value that has to go into SQL text
  through [`SqlText`](../src/DnnManager.Infrastructure/Sql/SqlText.cs) -
  `Identifier` (`[` + name with `]` doubled + `]`), `Literal`, `EscapeLike` - or
  checked first, like DNN's object qualifier (`Sql/DnnTables`). Connection
  strings only from `ConnectionStrings.For` (`ForSite` / `ForApp`), which decides
  encryption and certificate checks. A password reaches `sqlcmd` through
  `SQLCMDPASSWORD` or its standard input, never its command line.
- **Processes**: through `ProcessRunner` (a tool whose output is read) or
  `ElevatedStart` (the rest: SSMS, `vswhere`, Explorer), with argument lists -
  never a command line built from strings, never a shell. Both start only a
  program that nobody but administrators can change (`TrustedPrograms.Resolve`),
  with `ChildEnvironment`'s environment. A program for the user (an editor)
  starts as the user (`Unelevated.Start`, or `ElevatedStart.Explorer`), never
  with DNN Manager's rights. A quick question to a program that can hang gets a
  `timeout`. `LayeringTests` fails on `Process.Start` or `new ProcessStartInfo`
  in any other file - add one to its list only for DNN Manager's own exe.
- **Temporary files**: a file DNN Manager writes and later reads back or runs goes
  into `PrivateTemp.Path` (`IPrivateTemp` in Application) - never `%TEMP%`, which
  every program of the user's can change ([security.md](security.md#administrator-rights)).
- **Failures**: a use case notes in `OperationUndo` how to take back each step
  before it starts; the runner undoes it after a cancel *and* after a failure. An
  undo step takes a `CancellationToken` and honours it - it is cancelled when the
  step's time is up (`OperationUndo.StepLimit`, `FileStepLimit`, or a `limit` of
  its own) or the user stops the undo. To leave what a failed run made (to look
  into), call `OperationUndo.Keep()` before returning the failure
  ([ADR 0001](adr/0001-undo-on-failure.md)). Only an error that means "file in
  use" is worth trying again.
- **A database another site may use**: before dropping, replacing or taking over a
  database, ask `SiteDatabases.OtherSiteUsing`; a SQL Server address is read only
  through `SqlServerAddress`.
- **Secrets**: in the Windows Credential Manager or DPAPI-encrypted in the
  settings; never in a log line, a message, a command line written to disk, or
  the UI.
- **DNN Manager's own data**: through
  [`AppDatabase`](../src/DnnManager.Infrastructure/Data/AppDatabase.cs)
  (`Microsoft.Data.Sqlite`) - a new kind of record gets a table of its own,
  made by a new step at the end of `AppDatabase.Steps`; a new piece of workspace
  is a state area ([architecture.md](architecture.md#the-workspace-kept-between-starts)).
  Every value in a column or a row of its own - never JSON in the database, and
  never a file of its own in `Documents\DnnManager`. The native SQLite comes with
  `Microsoft.Data.Sqlite`; a test fails when it is older than 3.50.2, which fixed
  GHSA-2m69-gcr7-jv3q.
- **Tests** that save anything give `AppDataPaths` / `AppDatabase` a folder
  under `%TEMP%` - their own `dnnmanager.db`, never yours.

## Extending

- **New control style**: put it in the matching dictionary in
  `Themes/Controls/` - without `x:Key` to style every control of that type, or
  with one for a variant - and take sizes from `Tokens.xaml`. For a new kind of
  control, add a dictionary (a `.xaml` with `x:Class` and its partial class in
  `ControlDictionaries.cs`) and merge it in `App.xaml` after `Tokens` and
  `Icons`.
- **New icon**: add it to `Themes/Icons.xaml` - a font glyph as
  `<sys:String x:Key="Glyph…">&#xE…;</sys:String>`, a drawing as
  `<Geometry x:Key="…">` - and use it by its key, never inline.
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
  `ToAppOptions`, and give it a field on the Settings page - the settings aren't
  edited by hand. Nothing more: it is saved as a row of its own (its key the
  property's path in camelCase), and saved settings without that row get it with
  its default on the next start. Add its row to [configuration.md](configuration.md#the-settings).
- **Renaming a setting, moving it or changing what it means**: raise
  `UserSettings.CurrentVersion` and add code to
  [`SettingsStore`](../src/DnnManager.Infrastructure/Settings/SettingsStore.cs)
  that converts the rows of an older `version` before they are read - there is
  none yet. Without it the old row is no longer read and the setting starts
  from its default. Settings with a higher `version` than the build's
  aren't used (the start-up dialog).
- **Changing the tables** of `dnnmanager.db`: add a step at the end of
  `AppDatabase.Steps` - SQL that makes the new tables from the previous ones;
  it runs once in each database (`PRAGMA user_version` counts the steps run).
  Never change a step that has shipped.
- **Installer**: files, shortcuts and Setup options are in
  [`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss). Releases are built
  by the release workflow on GitHub, not on a PC - and signed there once signing
  is set up (it is off until its variables are set) - see
  [releasing.md](releasing.md#the-release-workflow) and [Code signing](releasing.md#code-signing).
- **Warnings are errors in CI** (`-warnaserror`, XML-comment warnings too): build
  with `-warnaserror` before you push ([releasing.md](releasing.md#the-build-workflow)).
- **Tests**: see [testing.md](testing.md#adding-a-test).
