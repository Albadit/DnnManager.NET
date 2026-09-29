# DnnManager.NET

**DNN Manager** (`dnnmanager.exe`) is a Windows desktop app for running DNN sites
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
- **Environment** - see whether Docker Desktop, its engine, the SQL Server
  container, the SQL Server connection and the IIS Windows features are active,
  and install / start / set up / enable what's missing.
- **Settings** - edit the settings from the app; each change is saved to
  `Documents\DnnManager\settings.json` as you make it.
- **Light and dark theme**, a live **activity log** with Cancel, and an eye
  button on every password field.

## Install

Run `DnnManagerSetup-<version>-x64.exe` (see [Build the installer](#build-the-installer)).
Like the Visual Studio Code user installer, it needs no administrator rights:

- installs into `%LOCALAPPDATA%\Programs\DnnManager` - **Browse…** picks another folder
- adds **DNN Manager** to the Start menu, and a desktop shortcut if you tick it
- registers in **Settings → Apps → Installed apps**, where you uninstall it
- starts the app when you click **Finish** (the app asks for administrator
  rights itself - UAC - every time it starts, since it manages IIS)

The app is self-contained: no .NET runtime to install. When DNN Manager is
already installed, Setup's first page shows the installed version and asks what
to do: **Repair**, which installs this Setup's version over it
in the same folder, or **Uninstall**, which runs the uninstaller and closes
Setup. Your settings are kept either way. If DNN Manager is running, Setup (and
the uninstaller) asks you to close it first. Silent installs (`/SILENT`,
`/VERYSILENT`) skip that page and upgrade in place.

`Setup.exe /ALLUSERS` installs for every user into `Program Files` instead (it
asks for administrator rights). For unattended installs, Inno Setup's
`/SILENT` / `/VERYSILENT` and `/DIR="..."` work too.

Your settings and backups are **not** in the install folder - they're in
`Documents\DnnManager` (see [Configuration](#configuration)). Setup never
writes there, so upgrading, reinstalling or uninstalling keeps them. To remove
them, delete that folder after uninstalling.

## Prerequisites

- Windows 10/11 or Windows Server (IIS available)
- **.NET 10 SDK** to build - <https://dotnet.microsoft.com/download/dotnet/10.0>
- Docker Desktop (Linux containers) for the shared SQL Server - set it up once
  with **Set up docker-compose** on the **Environment** page (or point the SQL
  Server settings at a SQL Server you already run)
- A user account that can elevate to Administrator (UAC prompt will appear)

## Build

All commands run from `DnnManager.NET\` (the folder containing `DnnManager.csproj`).

```bash
dotnet build                # Debug   -> bin\Debug\net10.0-windows\dnnmanager.exe
dotnet build -c Release     # Release -> bin\Release\net10.0-windows\dnnmanager.exe
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
.\bin\Release\net10.0-windows\dnnmanager.exe
```

### Option C - publish a single self-contained `.exe`

One file, no .NET runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PortableExe=true -o publish

.\publish\DnnManager-2.1.0-x64.exe
```

`-p:PortableExe=true` names the exe `DnnManager-<version>-x64.exe` (without it,
it's `dnnmanager.exe`). The publish output holds only the program. Settings live in
`Documents\DnnManager` and are created on first start (see
[Configuration](#configuration)).

> **"Access to the path '...\publish\dnnmanager.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

### Build the installer

```powershell
.\installer\build.ps1
```

Publishes the app (self-contained, single file) into `installer\bin\app`, then
compiles [`installer/DnnManager.iss`](installer/DnnManager.iss) with Inno Setup
into `publish\DnnManagerSetup-<version>-x64.exe`. The version
comes from `<Version>` in `DnnManager.csproj`. It uses an installed Inno Setup 6
when there is one, otherwise it downloads a pinned copy (the `Tools.InnoSetup`
package from nuget.org) into `installer\bin\tools` - no admin rights needed.
Everything made along the way (the published app, wizard images, Inno Setup)
is in `installer\bin`; the finished Setup is in `publish\`.
`-SkipPublish` reuses the last publish; `-Iscc <path>` picks the compiler.

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

VS Code tasks for build, publish, zip and the installer are in `.vscode/tasks.json`.

## Using the app

### Window

- **Sidebar** - the pages below, **Settings** (gear icon) at the bottom, and next
  to **Projects folder** a sun / moon button that switches between the light and
  dark theme.
- **Activity** - the log at the bottom shows each step of the running operation.
  It starts collapsed to its header bar, which still shows the running
  operation, its progress and **Cancel**; the chevron (or clicking **Activity**)
  opens the full log, where **Copy** puts it on the clipboard, and closes it
  again - it comes back at the height it had.
- **Dialogs** - questions from an operation (confirmations, e.g. before dropping
  a database) open as dialogs.
- Only one operation runs at a time. While it runs, the pages stay usable
  (scrolling, browsing), but starting a second one is refused.
- **Pages are kept** while the app runs: the Projects list, the Host project
  folders and the DNN versions load once, not on every visit. Their **Refresh**
  button loads them again, and so does every finished operation (a page not on
  screen catches up when it's next shown). Settings is read on every visit.
- **Toasts** - short messages over the bottom-right of the page, e.g. why the
  settings couldn't be saved. They fade out by themselves; warnings stay until
  closed.

### Pages

| Page | What it does |
|---|---|
| **Projects** | Table of every project folder: name, site URL, DNN version (from `bin\DotNetNuke.dll`), database, **SQL** (**Live** in green when the database is on the SQL Server, **Offline** in red when the server doesn't answer, *(none)* when the database doesn't exist), **IIS** (**Live** in green when the site is started, **Offline** in red when it isn't, *(none)* without a site), size and path. **Refresh** shows it's working (button reads *Refreshing…*, a bar runs along the table) and the subtitle shows when it last updated. The table scrolls both ways - **Shift + mouse wheel** scrolls sideways. **Open site** (or double-click a row) and **Remove…** act on the selected project; **right-click** a project for everything else (see [Project menu](#project-menu)). |
| **New project** | Enter a name (validated as you type), then **Start from**: *a new site* - pick the **Repository** (e.g. `dnnsoftware/Dnn.Platform`), then a **Version** from its GitHub releases (highest version first, the latest selected; *kept, no download* marks a version whose package is kept). The lists are loaded once, when the app starts - the refresh button next to Version asks GitHub again - or *an existing site* - pick the site `.zip` and its database `.bacpac` (see [Import a site .zip](#import-a-site-zip)). A name whose folder already exists is refused - set up an existing folder on **Host project**. |
| **Host project** | Pick a folder, then *IIS website + database* (the default), *database only* or *IIS website only*, and optionally a backup to restore. See [Host a project](#host-a-project). |
| **Clone project** | Copy a site from a local folder into a new project. See [Clone a project](#clone-a-project). |
| **Environment** | Three cards, each checked when you press its **Test** button (nothing runs on opening the page; an action re-tests what it changed), with a green / red status and a button to fix it. **Docker**: **Docker Desktop** (**Install Docker Desktop** via winget), the **Docker engine** (**Start Docker Desktop**, then waits for the engine) and the **SQL Server container**. **Set up docker-compose** runs the docker-compose.yml made from the settings (`docker compose up -d`, handed to Docker directly - no file is written, and it has the real SA password): it creates the container, starts it, or updates it after the settings changed, then waits for the sa login. **Show docker-compose.yml** shows the same file with a **Copy** button, to run yourself - without the SA password: replace `<your-sa-password>` after copying. **SQL Server**: the sa login to the host and port in Settings. **IIS**: the Windows features as a table with their status (**Enable missing features**). **Reset IIS** restarts IIS (`iisreset`, after a confirmation) - e.g. after installing the URL Rewrite module when a site shows *HTTP Error 500.19*. |
| **Settings** | Edit `settings.json`: projects, DNN releases (repositories, keeping downloaded packages), the **SQL Server** connection and the **Docker container**, each on a card of its own. Nothing is saved until **Save and restart** (in the bar that appears at the bottom once you change something), which saves and restarts DNN Manager. Testing the connection and setting up the Docker container happen on the **Environment** page. See [Configuration](#configuration). |

### Project menu

Right-click a project on the **Projects** page (or select it and press the
Menu key):

| Item | What it does |
|---|---|
| **Details…** | In sections, selectable, with **Copy all**: *Project* (folder, created, size, DNN version, git branch, solution, backups); *Website (IIS)* (Live/Offline status, URL, bindings, physical path - flagged when it isn't the project folder -, app pool and its state, .NET version, pipeline, identity); *Database* (Live/Offline, whether the database exists, server and login, size, the DNN version recorded in the database - flagged when it differs from the files -, portals and portal aliases); *web.config* (connection without the password, target framework, debug, custom errors, HTTPS redirects DNN Manager switched off). |
| **Open site** / **Open folder** | The site in the browser / the folder in Explorer. |
| **Copy path** | Puts the project folder on the clipboard. |
| **Open in** ▸ | A submenu with only the editors installed on the PC (greyed out when there are none): Visual Studio (via `vswhere`; opens the project's `.sln` when it has exactly one), VS Code, VS Code Insiders, Cursor, Windsurf, Rider, IntelliJ IDEA, Sublime Text, Zed, Vim (gVim, or console Vim in a window of its own) and Neovim (nvim-qt, or `nvim` in its own window) - found in their usual install folders or on PATH. Git for Windows' bundled vim doesn't count. |
| **Open in SQL Server Management Studio &lt;version&gt;** ▸ | One submenu per installed SSMS (21+ found via `vswhere`, 18-20 by their install folder): *Default* signs in to the local SQL Server from Settings as `sa`; *Project* signs in to the project's database - the one its `web.config` uses, or its local database as `sa`. SSMS only remembers the password when **Remember the password in SQL Server Management Studio** is on in Settings (off by default). When that SSMS is already open, the connection is added to it (via its *Connect Object Explorer...*) instead of starting another window. For the local container it also trusts the self-signed server certificate (`-C`, SSMS 21+). SSMS takes no password on its command line - and any connection switch makes it connect at once and fail - so for SSMS 21+ DNN Manager starts it without switches and fills in its Connect dialog through UI Automation (server, SQL Server Authentication, login, password with *Remember Password*, database, trust certificate, name) and clicks Connect - only in the SSMS it just started. Older SSMS gets the switches, and the password is left on the clipboard. A missing database opens the server instead. |
| **Export** ▸ | A backup into the project's folder in `Documents\DnnManager\backups` (see [Backups](#backups)): *Site and database* (`<project>.zip` + `<project>.bacpac`, the pair **New project** imports), *Site files* or *Database*. **Open backups folder** opens it in Explorer. |
| **Remove…** | After a confirmation, removes the IIS site and deletes the project folder - and drops its database if you say so. If files in the folder are still in use, it finds the programs holding them (open files, or a terminal / editor whose working folder is inside), lists them and - after you confirm - closes them and deletes the folder. Windows itself, services and Explorer are never closed; anything still locked is deleted at the next Windows restart. |

## Configuration

DNN Manager keeps your files apart from the program, in your **Documents**
folder, so updating, reinstalling or uninstalling the app never touches them:

```text
Documents\DnnManager\
├── settings.json        your settings
├── backups\             project backups (see Backups) and settings.json copies made before
│                        an upgrade of its format or a reset
├── logs\                the activity log, one file per day (kept 30 days)
└── packages\            downloaded DNN install packages, when projects.keepDnnPackages is on
```

The folder and `settings.json` are created the first time the app starts. When
you upgrade from 2.0 or earlier, the `appsettings.json`
next to the old DNN Manager 2.0 exe is carried over when the new
version is started from that same folder. After installing somewhere else, copy
`appsettings.json` into `Documents\DnnManager` as `settings.json` and it is
converted on the next start.

Edit the settings on the **Settings** page, then press **Save and restart**
in the bar that appears at the bottom of the page once you change something. The values are checked first (full
path, valid ports and URLs, required fields); a problem shows as a warning and
nothing is saved. The app reads settings at startup, so saving restarts it.
**Discard changes** puts the saved values back, and leaving the page or closing
the app with unsaved changes asks first. **Open settings.json** opens the file for the values the page doesn't
show, such as the IIS feature list.

```json
{
  "version": 2,
  "projects": {
    "baseDirectory": "C:\\DNN",
    "hostnameSuffix": "dnndev.me",
    "sitePort": 80,
    "dnnReleaseSources": [ "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases", "..." ],
    "keepDnnPackages": false
  },
  "sqlServer": {
    "host": "localhost",
    "port": 1433,
    "saPassword": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sB…"
  },
  "docker": {
    "containerName": "dnn-sqlserver",
    "volumeName": "dnn_sqlserver_data",
    "edition": "Developer",
    "collation": "Latin1_General_CI_AS"
  },
  "ssms": { "rememberPassword": false },
  "iis": { "requiredFeatures": [ { "name": "IIS-WebServerRole", "label": "IIS Web Server" }, "..." ] },
  "appearance": { "theme": "system" }
}
```

| Key | Meaning |
|---|---|
| `version` | The format of the file. Don't change it - the app upgrades older files itself. |
| `projects.baseDirectory` | Where projects live (`C:\DNN` by default). |
| `projects.sitePort`, `projects.hostnameSuffix` | Sites answer at `http://<project>.<hostnameSuffix>[:sitePort]`. |
| `projects.dnnReleaseSources` | GitHub releases API URLs - the repositories **New project** offers, with their versions. |
| `projects.keepDnnPackages` | `false` by default. When `true`, each downloaded DNN install package is kept in `Documents\DnnManager\packages\<owner>.<repo>\` and used again when a new project picks the same version - no download. When `false`, the package is downloaded into the project and deleted after installing. |
| `sqlServer.*` | The shared SQL Server DNN Manager connects to: `host` (`localhost` by default), `port` and `saPassword`. The password is stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - not hashed, since DNN Manager needs it to sign in. To change it in the file, replace the value with the new password as plain text; it's encrypted on the next start. A new project's database is named like the project. The Docker container publishes SQL Server on this port with this password. An existing data volume keeps the sa password it was created with. |
| `docker.*` | The SQL Server container: `containerName`, `volumeName`, `edition` (`MSSQL_PID`) and `collation`. **Environment → Set up docker-compose** makes the container from these (and `sqlServer.port` / `saPassword`); **Show docker-compose.yml** shows the file to copy. |
| `ssms.rememberPassword` | `false` by default. When `true`, signing SSMS in from the project menu ticks its *Remember Password*, so SSMS keeps the password. On the Settings page under **SQL Server**. |
| `iis.requiredFeatures` | IIS Windows features checked (and optionally enabled). |
| `appearance.theme` | `system` (follow the Windows app theme), `light` or `dark`. Set by the sidebar's theme button. |

When the app starts, it checks the file:

- **Missing keys** are added with their default values (and written back).
- **Invalid JSON, a value of the wrong type or a value that isn't allowed**
  (e.g. a port above 65535, an unknown theme) opens a dialog that says what's
  wrong, with **Try again** (after fixing the file), **Open file**, **Reset to
  defaults** (the current file is kept in `backups\`) and **Exit**. The app
  never starts with half-read settings.
- **An older format** is backed up to `backups\settings.v<n>.<time>.json`, then
  upgraded. A file from a *newer* DNN Manager is not touched - the dialog asks
  you to update the app or reset the settings.
- Keys the app doesn't know are kept when it saves.

Environment variables prefixed with `DNNMANAGER_` override settings, e.g.
`DNNMANAGER_DnnManager__Docker__SaPassword=...` (names as in `AppOptions`). The
Settings page lists any that are set, since they win over what it saves.

> The folder is the Documents folder of the Windows account the app runs as. If
> you sign in to the UAC prompt with a *different* administrator account, that
> account's Documents is used.

## Backups

All project backups are kept in one place, apart from the sites:
`Documents\DnnManager\backups\`, one folder per project and one dated folder per
backup:

```
Documents\DnnManager\backups\
├── ceesboer\
│   ├── ceesboer_20260928_154210\
│   │   ├── ceesboer.zip              ← the site files
│   │   └── ceesboer.bacpac           ← the database
│   └── ceesboer_20260930_091500\
│       └── ceesboer.bacpac           ← a database-only backup
└── settings.v0.20260929-101500.json  ← a settings.json copy (see Configuration)
```

Because they're outside the site, IIS never serves them, a site export never
includes them, and **removing a project keeps its backups**.

- **Projects → right-click → Export** writes a new dated folder (site, database
  or both); **Open backups folder** opens the project's folder. The site `.zip`
  leaves out `.git` and everything the site's `_backup.filter` lists (below).
- **Clone** keeps the source database backup it restored in a dated folder too.
- **New project → An existing site** lists every project with a complete backup
  (site + database) - including removed projects - and its backups by date. Pick
  one, or choose files anywhere on the PC.
- **Host project** offers the `.bacpac` / `.bak` files in the project's backups
  (and the project folder's root) to restore.

### `_backup.filter`

A `_backup.filter` in the project folder lists what the site `.zip` leaves out -
caches, search indexes, logs, big data files. It's the format Azure App Service
backups use, so the file a site already has for Azure works as it is: one path
per line, from `D:\home` (`\site\wwwroot\...` is the site root). A path without
that prefix counts from the site root, and a folder leaves out everything in it:

```text
\site\wwwroot\App_Data\Search
\site\wwwroot\App_Data\51Degrees.dat
\site\wwwroot\imagecache
Portals\_default\Logs
```

The activity log lists what was left out. `_backup.filter` itself is kept in the
zip, so a site imported from it has the same filter.

Backups made by DNN Manager 2.0 stay in each project's `01_backup` folder -
DNN Manager no longer uses it. Move the dated folders you want to keep to
`Documents\DnnManager\backups\<project>\`, then delete `01_backup` (otherwise it
is now included in the project's site `.zip`).

## Import a site .zip

**New project → Start from: an existing site** creates a project from a zipped
DNN site and its database backup - both required
([`ImportProjectUseCase`](src/DnnManager.Application/UseCases/ImportProjectUseCase.cs)).
**From a project backup** picks a project and one of its dated backups in
`Documents\DnnManager\backups`; the **Browse…** buttons take a `.zip` and `.bacpac` from anywhere:

1. **Extract** the zip into a new folder `<BaseDirectory>\<name>`. The site root
   is the zip's shallowest folder with a `web.config`, so a zip with everything
   under one top folder works too; files outside it are skipped. Entries that
   would land outside the folder are refused, and a failed or cancelled
   extraction removes the folder again. The zip isn't searched for a database.
2. **Host it** exactly like [Host a project](#host-a-project) with *IIS website +
   database*: IIS site, HTTPS redirects switched off, the `.bacpac` (a `.bak`
   works too) restored,
   `dbo.PortalAlias` pointed at the local hostname, DNN's SSL switched off in the
   database and `web.config` pointed at the database (after asking).

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
   otherwise one named like the folder. Then:
   - **with a backup**, restores it (`.bacpac` via SqlPackage, `.bak` via
     `RESTORE`) - asking first if the database already exists - and points
     `dbo.PortalAlias` at the local hostname so the site answers there. DNN's
     SSL (`SSLSetup`, or `SSLEnabled` / `SSLEnforced`, and pages marked secure)
     is switched off in the local database - the local site has no https, and
     DNN would otherwise redirect every request to `https://`;
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
2. **Source database** - always the `SiteSqlServer` connection in the source's
   `web.config`.
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
   hostname (see *Notes on cloning*), and switch DNN's SSL off in the copy, as
   Host project does.
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
│  Composition root (settings + Host + DI), admin elevation.          │
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
compiled into a single assembly (`dnnmanager.exe`).

```
DnnManager.NET/
├── DnnManager.csproj            ← single project (net10.0-windows, WPF WinExe)
├── app.manifest                 ← asInvoker; AdminElevation relaunches elevated
├── installer/
│   ├── DnnManager.iss           ← Inno Setup script (per-user install, shortcuts, uninstall)
│   ├── build.ps1                ← publish + compile the installer
│   └── bin/                     ← build files (published app, wizard images, Inno Setup) - not in git
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
    │   ├── Sql/                 ← sqlcmd in the container, remote backup, SqlPackage, connection test
    │   ├── Github/              ← GitHub API + DNN package downloader
    │   ├── Settings/            ← AppDataPaths (Documents\DnnManager), SettingsStore, SettingsMigrations
    │   ├── Files/               ← file copy, site .zip import / export, daily log file
    │   ├── Projects/            ← file-system project repository
    │   ├── Prereq/              ← IIS feature checks
    │   ├── WebConfigs/          ← web.config SiteSqlServer read / write
    │   ├── Processes/           ← shared ProcessRunner
    │   └── DependencyInjection.cs
    └── DnnManager.Presentation/
        ├── Program.cs           ← composition root (settings + Host + DI), starts WPF
        ├── AdminElevation.cs    ← relaunches elevated when needed
        ├── RunningMarker.cs     ← named mutex the installer checks before replacing the app
        ├── App.xaml             ← styles (buttons, inputs, lists, table, scrollbars, sidebar)
        ├── MainWindow.xaml      ← sidebar navigation + page host + activity log
        ├── Pages/               ← one page per sidebar item (incl. Settings)
        ├── Assets/              ← dnn.ico - the exe and window icon (DNN logo mark)
        ├── Controls/            ← InputDialog, DetailsDialog, MessageDialog, ExistingFolderOptions, PasswordInput
        ├── Themes/              ← LightTheme / DarkTheme colour palettes
        └── Services/            ← ActivityLog, OperationRunner, ThemeManager, IdeLocator, SettingsStartup, GUI adapters
```

### Key design decisions

| Decision | Why |
|---|---|
| **Clean Architecture (single project, layered folders)** | Use cases are testable without IIS/Docker; the UI was swapped from a terminal UI to WPF without touching business logic. Layers are enforced by namespace + folder convention. |
| **All side-effects behind interfaces** | `IIisManager`, `ISqlServerService`, `IDnnReleaseService`, `IPrerequisiteChecker`, `IWebConfigService`, `ISqlConnectionTester`, `IUserPrompt`, `IProgressReporter`, … Easy to mock in tests. |
| **`Result` / `Result<T>` instead of exceptions across layers** | Use-case outcomes are explicit; unexpected exceptions are still logged and surfaced centrally. |
| **`Microsoft.Extensions.Hosting` + `IOptions<AppOptions>`** | Standard DI and logging via `Microsoft.Extensions.Logging`. `AppOptions` is made from `settings.json` at startup, with `DNNMANAGER_*` env vars on top. |
| **Program and user data apart** | The installer owns the install folder; the app owns `Documents\DnnManager`. `settings.json` is versioned: `SettingsStore` backs it up and runs `SettingsMigrations` when its format is older, and fills in new keys from `UserSettings`' defaults. |
| **WPF, code-behind pages** | One `UserControl` per sidebar item, rebuilt on each visit so lists (folders, backups) are always fresh. |
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs, and backs the log's **Cancel** button. |
| **Adapters for GUI → app layer** | `GuiProgressReporter` (writes to the activity log) and `GuiUserPrompt` (modal dialogs) implement application interfaces, so use cases never know what drives them. |
| **Runtime theming** | Colours live in `LightTheme` / `DarkTheme`; everything references them with `DynamicResource`, and `ThemeManager` swaps the dictionary (and the title bar's dark mode) live. |
| **SQL** | The local container is checked by logging in with `Microsoft.Data.SqlClient` and driven with `sqlcmd` via `docker exec`; remote / Azure SQL uses `Microsoft.Data.SqlClient` and SqlPackage (`.bacpac`). |
| **Centralised error handling** | `OperationRunner` catches per-action exceptions and reports them in the activity log; `App` shows anything escaping a click handler; `Program.cs` catches fatal errors. |
| **Admin enforcement** | `AdminElevation` relaunches the app elevated (UAC prompt) when it isn't. |
| **No hardcoded values** | Container name, SA password, port, GitHub APIs, IIS feature list, hostname suffix, base directory, theme - all in `settings.json`. |

## Extending

- **New page**: add a `UserControl` under `Pages/` (Presentation) that runs its
  use case (Application, registered with DI) through `OperationRunner`, then add
  a sidebar entry in `MainWindow.xaml` and its type to the `Pages` map in
  `MainWindow.xaml.cs`. Reference colours with `DynamicResource` so the page
  follows the theme.
- **New colour**: add the same key to both `Themes/LightTheme.xaml` and
  `Themes/DarkTheme.xaml`.
- **New setting**: add the property, with its default, to a section of
  [`UserSettings`](src/DnnManager.Application/Configuration/UserSettings.cs)
  (and a check to `Validate` if it needs one), then carry it into `AppOptions` in
  `ToAppOptions`. Existing files get it with its default on the next start - no
  migration needed.
- **Changing the settings format** (renaming, moving or re-meaning a key): raise
  `UserSettings.CurrentVersion` and add an `ISettingsMigration` from the
  previous version to
  [`SettingsMigrations`](src/DnnManager.Infrastructure/Settings/SettingsMigrations.cs).
  The store backs the file up and runs the chain on the next start.
- **Installer**: files, shortcuts and Setup options are in
  [`installer/DnnManager.iss`](installer/DnnManager.iss). Code signing can be
  added there (`SignTool`) and in `build.ps1`.
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
| Environment (Docker, SQL Server, IIS features) | [`EnvironmentPage`](src/DnnManager.Presentation/Pages/EnvironmentPage.xaml.cs) + [`Prereq/WindowsPrerequisiteChecker.cs`](src/DnnManager.Infrastructure/Prereq/WindowsPrerequisiteChecker.cs) |
| Settings page (Save and restart) | [`SettingsPage`](src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs) |
| settings.json: format, defaults, validation | [`Configuration/UserSettings.cs`](src/DnnManager.Application/Configuration/UserSettings.cs) |
| settings.json: load, save, backups, migrations | [`Settings/SettingsStore.cs`](src/DnnManager.Infrastructure/Settings/SettingsStore.cs), [`Settings/SettingsMigrations.cs`](src/DnnManager.Infrastructure/Settings/SettingsMigrations.cs), [`Settings/AppDataPaths.cs`](src/DnnManager.Infrastructure/Settings/AppDataPaths.cs) |
| Settings error dialog at startup | [`Services/SettingsStartup.cs`](src/DnnManager.Presentation/Services/SettingsStartup.cs) |
| Installer | [`installer/DnnManager.iss`](installer/DnnManager.iss), [`installer/build.ps1`](installer/build.ps1) |
| Themes | [`Themes/`](src/DnnManager.Presentation/Themes/), [`Services/ThemeManager.cs`](src/DnnManager.Presentation/Services/ThemeManager.cs) |
| File copy, zip extract / create | [`Files/ProjectFileCopier.cs`](src/DnnManager.Infrastructure/Files/ProjectFileCopier.cs) |
| GitHub release lookup | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| sqlcmd | [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Shared SQL container | `docker-compose.yml` made from the settings by [`Docker/DockerComposeService.cs`](src/DnnManager.Infrastructure/Docker/DockerComposeService.cs) and run by [`UseCases/SetupSqlContainerUseCase.cs`](src/DnnManager.Application/UseCases/SetupSqlContainerUseCase.cs) (Environment → Set up docker-compose); the connection check is `LocalSqlContainer` in [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |

## Notes / limitations

- The dark title bar needs Windows 10 20H1 or later; older versions keep a light one.
- File pickers (Browse… / Save as) are standard Windows dialogs and follow the
  Windows theme, not the app's. Questions and warnings use the app's own dialog.
- The app runs as Administrator, so an IDE opened from the project menu does too
  (VS Code shows *[Administrator]* in its title).
- The SQL Server keeps the sa password its data volume was created with -
  changing **SA password** in Settings doesn't change it in an existing
  container.
- Cloning from a SQL Server on another machine (not Azure SQL) writes the
  `.bak` on that server, in this PC's temp path, so it only works when the source
  server runs on this machine. Azure SQL sources go through a `.bacpac` and work
  from anywhere.
