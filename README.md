# DnnManager.NET

**DNN Manager** (`DnnManager.exe`) is a Windows desktop app for running DNN sites
locally. It sets up new projects - and installs DNN in them for you -, imports
and exports sites as a `.zip` + `.bacpac`, hosts existing folders, clones sites from a local folder, and manages
their IIS websites and databases in a shared SQL Server (a Docker container it
can set up for you). It's a **WPF** app built on a **Clean Architecture**
solution.

## Features

- **Projects** - every DNN website configured in IIS, as a table: its live state,
  bindings, app pool, folder, CPU and memory, plus the DNN version and database
  of the folder it serves. IIS is the source - a site added, stopped or removed
  in IIS Manager shows up by itself, there is no Refresh. Open a site for its
  overview: IIS website and app pool, bindings, and for a DNN site **all the
  portals** of its installation with links to their addresses. Start, stop,
  restart or remove one site from its row, or several at once with the check
  boxes; search the list and choose the columns. Right-click a site to open it
  in an installed IDE or export it.
- **Status bar** - like Docker Desktop's: IIS running or stopped, with Start /
  Stop / Restart, then this PC's memory, CPU and disk use, the running
  operation with Cancel, the terminal's switch and the app's version.
- **Bottom panel** - like VS Code's, with three tabs: **Output** (every step
  of what DNN Manager does), **Logs** (a website's DNN, IIS and Windows logs,
  followed live) and **Terminal** (PowerShell, Command Prompt or Git Bash, as
  many as you open, also straight in a project's folder). **Ctrl+F** searches
  the shown tab; the panel can be maximized over the page.
- **Keep warm** - the flame in a site's row keeps it warm while DNN Manager
  runs, minimized too: IIS shuts an idle site down after 20 minutes, and its next
  page then takes seconds while DNN starts again. DNN Manager requests the
  site's tiny `KeepAlive.aspx` just often enough - not while it's in use - and
  warms it up again right after a recycle, like Azure's *Always On*. See
  [Keep warm](#keep-warm).
- **Light on resources while out of sight** - what only the window shows pauses
  (animations, this PC's figures, drawing terminals, following a log) and,
  while nothing runs, Windows runs DNN Manager in its efficiency mode. Sites
  are still followed; restoring the window brings everything up to date at once.
- **New project** - download a DNN release into a new folder, with its IIS
  site, hostname and database, and **install DNN for you**: the first visit
  shows the new site, with the host account you chose - not DNN's installation
  wizard (or choose *Manual DNN setup* and run the wizard yourself). The
  database can be the local SQL container, SQL Server / SQL Server Express
  (Windows or SQL Server authentication) or a LocalDB database file, tested
  before anything is created. Or import a `.zip` of an existing site plus its
  `.bacpac`.
- **Host project** - create the IIS site and/or database for a folder
  that's already there, optionally restoring a `.bacpac` / `.bak`.
- **Clone…** (a project's right-click menu) - copy a site (files + database)
  into a new project, including Azure SQL sources.
- **Settings** - edit the settings from the app; each change is saved to
  `Documents\DnnManager\settings.json` as you make it. Next to them, see
  whether Docker Desktop, its engine, the SQL Server container, the database
  server and the IIS Windows features are active, and install / start / set up /
  enable what's missing.
- **Light and dark theme**, **start at sign-in**, and an eye button on every
  password field.

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
[Configuration](#configuration)).

> **"Access to the path '...\publish\DnnManager.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

### Build the installer

```powershell
.\src\DnnManager.Installer\build.ps1
```

Publishes the app (self-contained, single file) into
`src\DnnManager.Installer\bin\app`, then compiles
[`src/DnnManager.Installer/DnnManager.iss`](src/DnnManager.Installer/DnnManager.iss) with Inno Setup
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

## Using the app

### Window

- **Title bar** - DNN Manager draws its own: next to the minimize / maximize /
  close buttons is **Settings** (gear icon, underlined while Settings is open -
  a page of its own, without the sidebar; the theme is chosen there). Drag it to move the window, double-click it to maximize; on Windows 11,
  resting on maximize shows Snap layouts.
- **Sidebar** - the pages below, and the **Projects folder** at the bottom. In a
  window narrower than 1100 pixels it slides to a narrow one with only the page
  icons (their names as tooltips).
- **Status bar** - along the bottom of the window, always visible, laid out
  like Docker Desktop's:
  - *under the sidebar*: **IIS running** / **stopped** in its colour (the IIS
    web service, W3SVC - Windows reports when it starts or stops, from wherever
    that is done) with **Restart** and **Stop**
    while it runs, **Start** while it's stopped (`iisreset`; stopping and
    restarting ask first - every site on the PC goes down). Restart is also what
    picks up IIS changes, e.g. a newly installed URL Rewrite module when a site
    shows *HTTP Error 500.19*. Under the narrow
    sidebar only its dot is left, with a **⋮** menu for the same actions;
  - *next*: this PC's **RAM** in use, **CPU** use (two decimals) and the
    **Disk** space used on the projects folder's drive, with its size as the
    *limit* - each keeps the room of its widest value, so the figures don't
    move as the numbers change (measured every 2 seconds, the disk every 10);
  - *while an operation runs*: its name, a progress bar and **Cancel** - click
    the name to open the panel on **Output**. **Cancel** puts everything back
    as it was before the operation started: what it made is taken away again,
    the last first - the IIS site and app pool, the database, the files and
    folders, an edited `web.config` - each step shown in **Output**. What can't
    be put back (a database or site that was there and was replaced as you
    chose, or what **Remove…** had already deleted) is named there;
  - *right*: the **>_** (terminal) button and the app's version.
- **Bottom panel** - opened and closed with the status bar's terminal button or
  **Ctrl+`** (it starts closed, and comes back at the height it had). Like VS
  Code's: its tabs on the left of its header - **Output**, **Logs**,
  **Terminal** -, then new terminal (on Terminal), clear (on Output), search,
  maximize and hide on the right. Copy and paste are **Ctrl+C** / **Ctrl+V** and
  the right-click menu, on every tab. The **maximize** button (or **Ctrl+Shift+M**) gives the panel the page's
  room - the tabs stay, the sidebar and status bar too -; again restores it.
  - **Output** - the log of each step of what DNN Manager does. It comes to the front when an operation
    starts, or when you click the running operation in the status bar.
  - **Logs** - a website's logs, one at a time: choose the site (every IIS
    site, by name) and the log at the top (the log's path, size and time next
    to them, with a button to open its folder). **View logs** on a site's right-click menu opens this tab with that
    log. Its newest 5,000 lines are read - never the whole of a large file -,
    then new lines appear as they are written (every second); the view follows
    them while it is at the bottom. Errors are red, warnings yellow. Long lines
    wrap at the panel's width (after the last space that fits), so there is no
    scrolling sideways; copying gives the lines without those breaks. Select text
    as in an editor - drag (across lines; past the edge it scrolls), Shift+click
    to extend, double-click a word, triple-click a line, **Ctrl+A** - and copy
    it with **Ctrl+C** or the right-click menu. The logs:
    *DNN* (`Portals\_default\Logs`, the newest 8), *IIS* (the site's request
    logs, the newest 8, and HTTP.sys's error log), *Windows* (ASP.NET errors and
    warnings about the site, its app pool's events, worker process crashes - from
    the event logs, new entries as they are logged).
  - **Terminal** - only shells; the open ones are listed on the right, each
    with its shell's icon - drag the list's left edge to make it wider or
    narrower; a terminal's bin shows while the mouse is on it. Opening the tab
    with none open starts one. **+** opens one with the default shell; the arrow next to
    it offers the shells installed on this PC: **PowerShell** (the default),
    **PowerShell 7**, **Command Prompt** and **Git Bash**. They start in the
    projects folder - or in a project's folder with **Open in terminal** on its
    right-click menu - and run with Administrator rights, like DNN Manager. They
    are real terminals (Windows' pseudo console): colours, tab completion and
    full-screen programs work. The scrollbar, the mouse wheel or **Shift + Page
    Up / Down** scroll back through the output, and typing jumps to the newest
    line again; drag to select, **Ctrl+C** copies a selection (and interrupts
    the program when nothing is selected), **Ctrl+V** or a right-click pastes.
    Rest the mouse on a tab for what it is: its shell, process ID, program
    (the `.exe`), the folder it started in and when. **Double-click** a tab (or
    **F2**, or right-click → **Rename**) to give it a name of your own. The
    **bin** on a tab - or `exit` - ends its shell; closing the panel doesn't.
  - **Search** - **Ctrl+F** (or the magnifier) opens a search bar over the shown
    tab: every match highlighted, the current one selected, *3 / 18* how many
    there are. In the box, like VS Code: **Aa** Match Case (**Alt+C**), **ab**
    Match Whole Word (**Alt+W**) and **.\*** Use Regular Expression (**Alt+R**) -
    a pattern that isn't valid says *Invalid regular expression* (why, in its
    tooltip). **Enter** / **F3** goes to the next one, **Shift+Enter** /
    **Shift+F3** to the previous one (around at the end); it starts at the
    newest. New output is searched as it comes, without jumping away from the
    current match. **Esc** or **✕** closes it and takes the highlights away -
    the text, and what runs in a terminal, aren't touched.
  The default shell, the font and its size, and whether terminals are offered
  at all (without them the Terminal tab is hidden) are in **Settings → General**.
- **Dialogs** - questions from an operation (confirmations, e.g. before dropping
  a database) open as dialogs.
- **One DNN Manager at a time.** Starting it while it's already open (Start
  menu, shortcut, the exe) brings the open window to the front - restored if it
  was minimized - instead of opening a second one, and doesn't ask for
  Administrator rights again.
- Only one operation runs at a time. While it runs, the pages stay usable
  (scrolling, browsing), but starting a second one is refused.
- **Pages are kept** while the app runs: the Host project folders and the DNN
  versions load once, not on every visit. Their **Refresh** button loads them
  again, and so does every finished operation (a page not on screen catches up
  when it's next shown). **Projects** has no Refresh - it keeps itself up to
  date (see [Projects table](#projects-table)). Settings is read on every visit.
- **Toasts** - short messages over the bottom-right of the page, e.g. why an
  operation failed (with **Show output**, which opens its log) or why the
  settings couldn't be saved. They fade out by themselves; warnings and errors
  stay until closed.

### Efficiency mode (while the window can't be seen)

A window that can't be seen - minimized, or completely covered by other windows,
on another virtual desktop or behind a locked screen - shows nothing, so DNN
Manager stops spending on it - **Settings → General → Save resources while the
window can't be seen**, on by default. Whether it is covered is decided as
Chromium apps such as Docker Desktop and Edge decide it (Chromium's
`NativeWindowOcclusionTracker`): while the window isn't in front, Windows' window
events have it look again 100 ms after they settle, and only opaque, ordinary
windows count as covering it (not tool windows, see-through or click-through
ones, or ones of an irregular shape).

- **Paused** - the progress bars and a changing site's pulsing dot (WPF draws
  about 60 frames a second for them, seen or not), this PC's figures in the
  status bar, drawing a terminal (its output is still read, so the shell never
  waits), following a log file, and folder-size walks. A toast that comes
  meanwhile waits to be seen before it fades.
- **Not paused** - what Windows reports about IIS and the projects folder, the
  sites' reconciliation every 30 seconds (a site stopped meanwhile is noted in
  **Output**), [keep warm](#keep-warm)'s requests, operations with their progress
  and log lines, the log file.
- **Efficiency mode** - once the window hasn't been seen for a second, no
  operation runs (or ended in the last 5) and no terminal printed for 10,
  DNN Manager goes into Windows' *Efficiency mode* - EcoQoS and Idle priority,
  as Task Manager sets it, which shows the leaf next to it. Terminal shells
  always start at Normal priority, and anything opened from the window is
  opened after it has ended. It ends at once when an operation
  starts, a terminal prints or the window can be seen again, so operations
  always run at full speed. Task Manager also writes *Efficiency mode* in the
  Status column, because DNN Manager's own process is in it; Chromium apps only
  show the leaf there because they put their child processes in it, never the
  main one - DNN Manager's work is in its main process.
- **Seen again**, everything is brought up to date at once, without a loading screen:
  the table reads what changed, the figures come within about a second, a log
  shows every line written meanwhile, a terminal is drawn again.

Turned off, everything goes on while the window is minimized, as when it is
shown. On Windows 10 the request gets the milder *low QoS*; on battery, Windows
also slows minimized apps down by itself.

### Pages

| Page | What it does |
|---|---|
| **Projects** | A table of every DNN website in IIS, one row per site - see [Projects table](#projects-table) and [Site overview](#site-overview). |
| **New project** | Enter a name (validated as you type), then **Start from**: *a new site* - pick the **Repository** (e.g. `dnnsoftware/Dnn.Platform`), then a **Version** from its GitHub releases (highest version first; pre-releases are listed too, marked *(pre-release)*, but the latest release - marked *(latest)* - is what's selected; *kept, no download* marks a version whose package is kept). The lists are loaded once, when the app starts - the refresh button next to Version asks GitHub again - or *an existing site* - pick the site `.zip` and its database `.bacpac` (see [Import a site .zip](#import-a-site-zip)). A name whose folder already exists is refused - set up an existing folder on **Host project**. For a new site: **IIS** - the host name and port it answers on; **DNN installation** - *Automatic setup* (the default) or *Manual DNN setup*; **DNN account and website** (automatic setup) - host username and password (**Generate** makes one), e-mail, website name, language and site template, filled in from **Settings → Projects → DNN defaults**. The database is named like the project and goes on the server from **Settings → Database server** (a LocalDB file is the site's own `App_Data\Database.mdf`); it is tested before anything is created. Host name and website name follow the project's name until you type your own. **Create project** is ready once everything is valid. See [Automatic DNN setup](#automatic-dnn-setup). |
| **Host project** | Pick a folder, then *IIS website + database* (the default), *database only* or *IIS website only*, and optionally a backup to restore. See [Host a project](#host-a-project). |
| **Test and set up** (Settings → Docker container, Database server and IIS) | A card at the end of each of those categories, checked when you press its **Test** button (nothing runs on opening it; an action re-tests what it changed), with a green / red status and a button to fix it. **Docker**: **Docker Desktop** (**Install Docker Desktop** via winget), the **Docker engine** (**Start Docker Desktop**, then waits for the engine) and the **SQL Server container**. **Set up docker-compose** runs the docker-compose.yml made from the settings (`docker compose up -d`, handed to Docker directly - no file is written, and it has the real SA password): it creates the container, starts it, or updates it after the settings changed, then waits for the sa login. The compose project is `dnn-mssql`; a container made by an older DNN Manager under `dnn-shared` is removed and made again under the new name - the databases stay, they're in the volume. **Show docker-compose.yml** shows the same file with a **Copy** button, to run yourself - without the SA password: replace `<your-sa-password>` after copying. **Database server**: the server from **Settings → Database server**, whichever connection type it is - it answers, the sign-in works, its version, and whether the login may create the databases new projects get. **IIS**: the Windows features as a table with their status. **Set up IIS** checks them and, after a confirmation, enables the missing ones (a reboot may be needed). Restarting IIS is on the status bar. |
| **Settings** (the gear in the title bar) | Edits `settings.json` on a page of its own, laid out like Docker Desktop's settings: the categories on the left, under a **search** box that leaves the ones with a matching setting (its **✕** empties it), and the chosen category on the right. **General**: **Start DNN Manager when you sign in** (a scheduled task that starts it with its Administrator rights, so Windows doesn't ask for them at every sign-in), the theme (*Light*, *Dark* or *Use system settings*), the **UI scale** (80-175 %, everything bigger or smaller like a browser's zoom) and **font size** (11-18 px, only the text), **Save resources while the window can't be seen** (see [Efficiency mode](#efficiency-mode-while-the-window-cant-be-seen)), and the **terminal** (on or off, the default shell, font family and size), and **Reset to defaults…** - every setting in every category filled in as DNN Manager is installed (the saved passwords and starting at sign-in too), saved only with **Save** (the old `settings.json` is copied to `backups`) and taken back with **Discard changes**. **Projects**: the projects folder, hostname suffix and site port, the **DNN defaults** new projects start with (install mode, host username and password, e-mail, website name, language, site template - the password is kept in the Windows Credential Manager, not in `settings.json`), and **Keep warm** - the interval, keep-alive page and warm-up page of the sites kept warm (see [Keep warm](#keep-warm)). **DNN releases**: the repositories, and keeping downloaded packages. **Database server**: the **connection type** new projects get their database on - the *Local SQL container (Docker)* (host, port, SA password), *SQL Server / SQL Server Express* (server, Windows or SQL Server authentication, login - its password kept in the Windows Credential Manager) or a *SQL Server Express LocalDB (file)* (the LocalDB instance); only the chosen type's settings are saved, the others keep what they had - and remembering the password in SSMS. **Docker container**: its name, volume, edition and collation. **IIS**: the Windows features DNN Manager needs (the list is edited in the file). **About**: the version and the folders with your files (settings, backups, logs, DNN packages), each with **Open**. Nothing is saved until **Save** (bottom right, ready once you change something in any category; **Discard changes** puts the saved values back), which saves the settings and applies them at once - no restart. **Close** (or the ✕) goes back to the page you came from. **Docker container**, **Database server** and **IIS** each end with their **Test and set up** card - **Test**, **Set up docker-compose**, **Set up IIS** and the rest - working with the saved settings (while there are unsaved changes they wait for **Save**). See [Configuration](#configuration). |
| **Troubleshoot** (the bug next to the gear) | Laid out like Docker Desktop's: **Restart** closes DNN Manager (asking first, as on any quit) and starts it again - projects, settings and data are kept. **Clean up data** deletes what is ticked from `Documents\DnnManager`, each with its size: the logs, the kept DNN packages, the settings copies and - never ticked for you - the project backups. **Reset to factory defaults** puts DNN Manager back as it was installed: the settings (a copy is kept in `backups`), the saved passwords, starting at sign-in, which sites are kept warm, the logs and the kept packages go, then it restarts. Your projects - their IIS sites, folders and databases - and their backups are never touched. Not while an operation runs. |

### Projects table

Every DNN website configured in IIS is a row - IIS is what the table shows, not
the projects folder; a site whose folder has no DNN install (`bin\DotNetNuke.dll`),
like IIS's own *Default Web Site*, isn't listed. The projects folder is only where **New project** and **Host
project** set sites up (and the only place **Remove…** deletes files from). Each
row: a **check box**, a **chevron** that opens the row's details (address,
physical path, app pool, bindings, site ID, database, DNN version, worker
process, keep warm), its **status** dot, its **name** (a link to the site's
[overview](#site-overview)), the columns you chose and its **Actions**. The
address is IIS's: an https binding with a certificate first, then http. The DNN
version and the database come from the folder the site serves (`bin\DotNetNuke.dll`
and its `web.config`) - a site that isn't DNN shows *(none)* and *-*.

- **Always current** - the table has no Refresh button: it follows the
  system. A site started or stopped in IIS Manager, IIS restarted, a worker
  process that ended, a site added or removed in IIS - each shows up by itself,
  in the row it is about, within seconds. Nothing else
  is touched: the checked rows, the search, the sort order, the scroll position
  and the open details stay as they are, and the only *Loading projects…* is
  the one when the app starts. Changes made outside DNN Manager are noted in
  the activity log. Only when something can't be read (IIS's configuration,
  the projects folder), or after the PC wakes up, *Reconnecting…* shows top
  right: the table stays as it was and catches up by itself. Rest the mouse
  on it for the reason and the time of the last synchronisation. How it works
  is under [Live updates](#live-updates).
- **Status** - a green dot while the IIS site runs, a hollow ring while it's
  stopped (also when only its app pool is: *App pool stopped*), amber while
  it's starting, stopping or an action on it runs, a dash when the project has
  no IIS site.
- **Actions** - **Stop** and **Restart** for a running site, **Start** for a
  stopped one, the **flame** that switches [keep warm](#keep-warm) on and off
  (its look and tooltip say how it is going), and **Remove…** (the bin) for any
  project - the site's tools (see below) are on its right-click menu. Stop also stops the
  site's app pool (unless another site uses it), which ends its worker process;
  Restart recycles the app pool; Start starts the pool and the site. The row
  says *Starting…*, *Stopping…* or *Restarting…* at once (*Removing…* once you
  have confirmed), with a progress bar, and every action waits for it (one
  operation runs at a time); afterwards it shows what IIS reports. A stopped
  site stays *Stopping…* until its worker process has ended - only then can it
  be started again. When an action fails, the row goes back to its real state
  and a toast says why. When IIS itself is stopped, start it from the status
  bar first.
- **Check boxes** - check rows (or press **Space** on the selected row) and the
  bulk actions appear above the table as one group of icons: **Remove…** (the
  red bin, on the left), then **Start**, **Stop** and **Restart** - rest the
  mouse on one for what it will do. Each acts on the checked rows it applies to - Start on the
  stopped ones, Stop and Restart on the running ones - and is greyed out when
  none of them qualifies. **Remove…** asks once for all of them (drop the
  databases too? remove them permanently?). The header check box checks every
  row the search shows, or none; it shows a dash when some are checked. Under
  the table: *12 projects* (or *4 of 12 projects* while searching) and
  *Selected 2 of 12*.
- **Search** - filters as you type on the name, site URL, site ID, port,
  status, DNN version, database and path; the **✕** in the box (or **Esc**)
  clears it. The bulk buttons
  act only on checked rows the search shows.
- **Only show running** (the switch next to the Columns button) - leaves the
  projects whose site is running; a row goes when its site stops and comes
  back when it starts.
- **Columns** (the button next to the search) - switch the optional columns on
  and off. They are listed, and shown, the most used first: *DNN version*,
  *Database*, *SQL* (**Live** in green when the database is on the SQL Server,
  **Offline** in red when the server doesn't answer, *(none)* when the database
  doesn't exist), *CPU (%)* (share of the whole PC), *Memory usage*, *PID*
  (what a debugger attaches to) and *Last started* (when the worker process
  started - IIS starts one on the site's first request) - these seven are the
  default, and **Default** in the menu goes back to them. Then *Status* (as
  text), *Site* (URL), *Port(s)*, *Site ID*, *Size*, *Memory (%)*,
  *Disk read/write* (bytes the worker process read and wrote since it started -
  its files, and its database connection), *Network I/O* (bytes the site
  received / sent over HTTP since IIS started - IIS's own counters) and *Path*.
  The choice is saved in `settings.json`. The CPU, memory, disk and PID columns
  show *-* while the site has no worker process.
- The check box, chevron, status and name stay on the left and **Actions** on
  the right while the columns between them scroll sideways - scrollbar or
  **Shift + mouse wheel** - so a narrow window still shows every row's actions. Click a
  column header to sort; the table stays sorted as values change - a row whose
  status or CPU use changes moves to its place. Click a site's **name**,
  **double-click** its row or press **Enter** to open its
  [overview](#site-overview); **right-click** it for everything else (see
  [Project menu](#project-menu)).

### Site tools

On a site's right-click menu (and the **⋮** on its overview):

- **Clear website cache…** - after a confirmation, deletes DNN's cached files
  (`Portals\_default\Cache`) and bundled CSS / JavaScript
  (`App_Data\ClientDependency`) - the folders stay; files in use are left - and
  recycles the site's app pool, which empties what it holds in memory. For a
  site that isn't DNN only the app pool is recycled. The row says *Clearing
  cache…* meanwhile, a notification says when it's done (or why it failed); the
  table isn't reloaded.
- **View logs** ▸ - the site's logs under their kind (*DNN*, *IIS*, *Windows*;
  the path in the tooltip). One opens on the bottom panel's **Logs** tab, not in
  a terminal.

### Site overview

Opened from the table, in its place (**←** or **Esc** goes back, with the table
as it was). At the top the site's name, state and address, with **Start** /
**Stop** / **Restart**, **Open site**, **Open folder** and **⋮** (the [site
tools](#site-tools)) - all live, like the table. Below, five tabs, one shown at
a time:

- **General** - the folder (and whether it is in the projects folder), when it
  was made, its size, the DNN version, git branch, solution and backups.
- **IIS** - the **IIS website** (website status, application pool and its
  state, physical path, site ID, worker process, and the app pool's .NET
  version, pipeline and identity), **Keep warm** (its switch, how it is going
  with **Check now**, the app pool's idle time-out, how often the site is
  requested and which pages, and the site's own interval and pages - see
  [Keep warm](#keep-warm)) and its **Bindings** (protocol, address as a link,
  port, IP address and SSL - certificate assigned or not).
- **DNN** - how DNN was installed (*automatic setup by DNN Manager* and when,
  *manual, DNN's installation wizard*, or *installed before DNN Manager kept
  track*), the DNN version, the host account - username and e-mail, never a
  password -, and the first portal's name and alias. **Change host password…**
  sets a new one (see [Changing the host password](#changing-the-host-password)).
  Then the **DNN portals**: every portal of the installation, read from its
  database - one IIS site can serve several. Each with its ID, name and status
  (*Active* / *Expired*), its primary alias as a link that opens in your
  browser (https when the site has an https binding for that host), and its
  other aliases behind **Show … more aliases**.
- **Database** - the connection type (local SQL container, SQL Server, LocalDB
  file), server, database and authentication (a SQL login's name, never its
  password), its state, size, portal count and the DNN version recorded in it.
  **Test connection** runs the same checks Create project runs before it creates a site.
- **Advanced** - the `web.config`: its path, the connection (without its
  password), DNN's `InstallVersion`, target framework, debug, custom errors and
  the HTTPS redirects DNN Manager switched off.

### Project menu

Right-click a project on the **Projects** page (or select it and press the
Menu key):

| Item | What it does |
|---|---|
| **Start** / **Stop**, **Restart** | The same as the row's actions, for the site's current state. |
| **Keep warm** / **Stop keeping warm** | Switches [keep warm](#keep-warm) on or off for the site - the same as the flame in its row. |
| **Open with…** ▸ | A submenu with only what is installed on the PC (greyed out when there is nothing). First the editors - Visual Studio (via `vswhere`; opens the project's `.sln` when it has exactly one), VS Code, VS Code Insiders, Cursor, Windsurf, Rider, IntelliJ IDEA, Sublime Text, Zed, Vim (gVim, or console Vim in a window of its own) and Neovim (nvim-qt, or `nvim` in its own window) - found in their usual install folders or on PATH. Git for Windows' bundled vim doesn't count. Then one entry per installed SQL Server Management Studio, each a submenu (21+ found via `vswhere`, 18-20 by their install folder): *Default* signs in to the local SQL Server from Settings as `sa`; *Project* signs in to the project's database - the one its `web.config` uses, or its local database as `sa`. SSMS only remembers the password when **Remember the password in SQL Server Management Studio** is on in Settings (off by default). When that SSMS is already open, the connection is added to it (via its *Connect Object Explorer...*) instead of starting another window. For the local container it also trusts the self-signed server certificate (`-C`, SSMS 21+). SSMS takes no password on its command line - and any connection switch makes it connect at once and fail - so for SSMS 21+ DNN Manager starts it without switches and fills in its Connect dialog through UI Automation (server, SQL Server Authentication, login, password with *Remember Password*, database, trust certificate, name) and clicks Connect - only in the SSMS it just started. Older SSMS gets the switches, and the password is left on the clipboard. A missing database opens the server instead. |
| **Details…** | The site's [overview](#site-overview) - the same as clicking its name. |
| **Open site** / **Open folder** | The site in the browser / the folder in Explorer. |
| **Open in terminal** | A new terminal (the default shell) in the project's folder, in the terminal panel. Not shown when the terminal is switched off in Settings. |
| **Clear website cache…**, **View logs** ▸ | The [site tools](#site-tools). |
| **Clone…** | A copy of the project - its files, its database and an IIS website of its own - under a new name. See [Clone a project](#clone-a-project). |
| **Export** ▸ | A backup into the project's folder in `Documents\DnnManager\backups` (see [Backups](#backups)): *Site and database* (`<project>.zip` + `<project>.bacpac`, the pair **New project** imports), *Site files* or *Database*. **Open backups folder** opens it in Explorer. |
| **Remove…** | After a confirmation, removes the IIS site and - for a site in the projects folder - deletes its folder, and drops its database if you say so. A site whose folder is elsewhere (IIS's *Default Web Site*…) only loses its IIS site: its files are kept, and only the database its web.config names can be dropped. If files in the folder are still in use, it finds the programs holding them (open files, or a terminal / editor whose working folder is inside), lists them and - after you confirm - closes them and deletes the folder. Windows itself, services and Explorer are never closed; anything still locked is deleted at the next Windows restart. |

## Configuration

DNN Manager keeps your files apart from the program, in your **Documents**
folder, so updating, reinstalling or uninstalling the app never touches them:

```text
Documents\DnnManager\
├── settings.json        your settings
├── backups\             project backups (see Backups) and settings.json copies made before
│                        an upgrade of its format or a reset
├── logs\                the activity log, one file per day (kept 30 days)
├── packages\            downloaded DNN install packages, when projects.keepDnnPackages is on
└── projects\            how DNN Manager installed the projects it set up, one file each
    └── keep-warm\       the sites kept warm, with their own keep-warm values, one file each
```

The folder and `settings.json` are created the first time the app starts. When
you upgrade from 2.0 or earlier, the `appsettings.json`
next to the old DNN Manager 2.0 exe is carried over when the new
version is started from that same folder. After installing somewhere else, copy
`appsettings.json` into `Documents\DnnManager` as `settings.json` and it is
converted on the next start.

Edit the settings on the **Settings** page, then press **Save** at the bottom
of the page. The values are checked first (full path, valid ports and URLs,
required fields); a problem shows as a warning and nothing is saved. Saved
settings apply at once, without a restart: another projects folder shows its
projects in the table, a new site address or SQL Server is what the next
operation uses. (Not while an operation runs - save once it has finished.)
**Discard changes** puts the saved values back, and leaving the page or closing
the app with unsaved changes asks first. The values the page doesn't show, such
as the IIS feature list, are edited in `settings.json` itself (**Settings →
About** opens its folder) - such a change applies the next time DNN Manager
starts.

```json
{
  "version": 3,
  "projects": {
    "baseDirectory": "C:\\DNN",
    "hostnameSuffix": "dnndev.me",
    "sitePort": 80,
    "dnnReleaseSources": [ "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases", "..." ],
    "keepDnnPackages": false,
    "dnnDefaults": {
      "installMode": "automatic",
      "hostUsername": "host",
      "hostEmail": "admin@admin.com",
      "websiteName": "My Website",
      "language": "en-US",
      "template": "Default Website"
    },
    "keepWarm": { "pingMinutes": 5, "warmUpPath": "/", "pingPath": "/KeepAlive.aspx" }
  },
  "sqlServer": {
    "type": "container",
    "host": "localhost",
    "port": 1433,
    "saPassword": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sB…",
    "server": ".\\SQLEXPRESS",
    "authentication": "windows",
    "userName": ""
  },
  "docker": {
    "containerName": "dnn-sqlserver",
    "volumeName": "dnn_sqlserver_data",
    "edition": "Developer",
    "collation": "Latin1_General_CI_AS"
  },
  "ssms": { "rememberPassword": false },
  "iis": { "requiredFeatures": [ { "name": "IIS-WebServerRole", "label": "IIS Web Server" }, "..." ] },
  "appearance": { "theme": "system", "uiScale": 100, "fontSize": 13, "projectColumns": [ "url", "dnn", "database", "sql", "cpu", "memory", "pid", "lastStarted" ] },
  "terminal": { "enabled": true, "defaultShell": "powershell", "fontFamily": "", "fontSize": 13 },
  "window": { "saveResourcesWhileMinimized": true }
}
```

| Key | Meaning |
|---|---|
| `version` | The format of the file. Don't change it - the app upgrades older files itself. |
| `projects.baseDirectory` | Where projects live (`C:\DNN` by default). |
| `projects.sitePort`, `projects.hostnameSuffix` | Sites answer at `http://<project>.<hostnameSuffix>[:sitePort]`. |
| `projects.dnnReleaseSources` | GitHub releases API URLs - the repositories **New project** offers, with their versions. |
| `projects.keepDnnPackages` | `false` by default. When `true`, each downloaded DNN install package is kept in `Documents\DnnManager\packages\<owner>.<repo>\` and used again when a new project picks the same version - no download. When `false`, the package is downloaded into the project and deleted after installing. |
| `projects.dnnDefaults.*` | What **New project** starts with for a new site: `installMode` (`automatic` or `manual`), `hostUsername` (`host`), `hostEmail` (`admin@admin.com`; empty: `host@<hostnameSuffix>`), `websiteName` (`My Website`; empty: the project's name), `language` (`en-US`, `de-DE`, `es-ES`, `fr-FR`, `it-IT` or `nl-NL`) and `template` (`Default Website` or `Blank Website`). The host password is not in the file: it is in the Windows Credential Manager of your account (`DnnManager/dnn-defaults/host-password`); while none is saved there it is `Admin@123`. Set in **Settings → Projects**. |
| `projects.keepWarm.*` | How the sites switched to [keep warm](#keep-warm) are kept warm, unless a site has its own values (its overview, **IIS**): `pingMinutes` (`5`, 1 to 60) - the longest wait between two requests, shortened for a site whose app pool idles out sooner; `pingPath` (`/KeepAlive.aspx`) - the page requested to keep a running site warm (`/` keeps the home page's caches warm too, as Azure's *Always On* does); `warmUpPath` (`/`) - the page requested to warm up a site without a worker process. Pages are on the site itself (a leading `/` is added), never one of DNN's installer (`/Install/…`, `mode=`). Set in **Settings → Projects → Keep warm**. Which sites are kept warm is not in the file: it is in `projects\keep-warm\`. |
| `sqlServer.type` | Where a new project's database goes: `container` (the local SQL container - the default), `sqlServer` (SQL Server / SQL Server Express at `server`, with `authentication` `windows` or `sql` and, for `sql`, the login `userName` - its password is in the Windows Credential Manager, `DnnManager/database-server/password`) or `localDbFile` (the site's own `App_Data\Database.mdf` on the LocalDB instance `server`, e.g. `(LocalDB)\MSSQLLocalDB`). Set in **Settings → Database server**; a project keeps the database it was made with. |
| `sqlServer.host`, `port`, `saPassword` | The local SQL container DNN Manager connects to: `host` (`localhost` by default), `port` and `saPassword`. The password is stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - not hashed, since DNN Manager needs it to sign in. To change it in the file, replace the value with the new password as plain text; it's encrypted on the next start. A new project's database is named like the project. The Docker container publishes SQL Server on this port with this password. An existing data volume keeps the sa password it was created with. |
| `docker.*` | The SQL Server container: `containerName`, `volumeName`, `edition` (`MSSQL_PID`) and `collation`. **Settings → Docker container → Set up docker-compose** makes the container from these (and `sqlServer.port` / `saPassword`); **Show docker-compose.yml** shows the file to copy. |
| `ssms.rememberPassword` | `false` by default. When `true`, signing SSMS in from the project menu ticks its *Remember Password*, so SSMS keeps the password. On the Settings page under **Database server**. |
| `iis.requiredFeatures` | IIS Windows features checked (and optionally enabled). |
| `appearance.theme` | `system` (follow the Windows app theme), `light` or `dark`. Set in **Settings → General**. |
| `appearance.uiScale` | `100` by default: everything in the window - text, icons and spacing, menus and tooltips too - at this percentage (50 to 200; Settings offers 80-175). Set in **Settings → General**, applied at once. |
| `appearance.fontSize` | `13` by default: the app's text size in pixels (8 to 32; Settings offers 11-18) - titles and hints keep their proportions, icons keep their size. The terminal has its own (`terminal.fontSize`). Set in **Settings → General**, applied at once. |
| `appearance.projectColumns` | The optional columns the Projects table shows: `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`, `status`, `url`, `ports`, `id`, `size`, `memoryPercent`, `disk`, `network`, `path`. Set by the table's **Columns** button; the default is `url`, `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`. |
| `terminal.*` | The terminal panel, set in **Settings → General** and applied at once: `enabled` (`false`: no Terminal tab, no shells), `defaultShell` (`powershell`, `pwsh`, `cmd` or `gitbash` - the first installed one when that one isn't), `fontFamily` (empty for Cascadia Mono, or Consolas) and `fontSize` (8 to 32) - also the font of the Output and Logs tabs. |
| `window.saveResourcesWhileMinimized` | `true` by default: while the window is minimized, what only it shows pauses and, while nothing runs, Windows runs DNN Manager power-efficiently - see [Efficiency mode](#efficiency-mode-while-the-window-cant-be-seen). `false`: everything goes on as while the window is shown. Set in **Settings → General**, applied at once. |

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

## Automatic DNN setup

**New project → Start from: a new site** with *Automatic setup* (the default)
installs DNN the way DNN's own unattended install does - not by clicking
through its wizard -, so the first visit shows the new site
([`SetupProjectUseCase`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs),
[`DnnInstaller`](src/DnnManager.Infrastructure/Dnn/DnnInstaller.cs)). What can
be checked is checked before anything is created: the project's name and
folder (a site path longer than 100 characters is refused - DNN's packages fail
to install below it), the host account (a password of 7 to 128 characters,
without `<` or `&#`: DNN finishes "successfully" without a host for a shorter
one, and its login form refuses those characters), IIS, and the database -
reachable, signed in, a SQL Server version the release supports, and allowed to
create the database (or owning it, empty, when it exists).

The **Output** tab follows it step by step:

1. **Testing database connection** - each check, as the overview's **Test connection** shows
   them.
2. **Creating project directory**, **Downloading DNN** - the release is
   extracted into the folder.
3. **Creating IIS application pool and website** - bound to the host name and
   port you chose. For a LocalDB file the app pool loads its user profile
   (LocalDB needs it).
4. **Creating database** - with Windows authentication the site's app pool
   identity (`IIS APPPOOL\<project>`) gets a login and owns the database.
5. **Configuring DNN** - `web.config`'s `SiteSqlServer` points at the database,
   and `Install\DotNetNuke.install.config` is written from the package's own
   template: the host account, website name, language, site template and the
   portal alias (`<host name>[:port]`).
6. **Running DNN installation** - DNN's `Install/Install.aspx?mode=install`,
   requested by DNN Manager on this machine (with the site's host name, so no
   DNS or hosts entry is needed), its progress shown as it comes: the database
   scripts, each package (*installing … (12 of 47)*), **Creating portal**. DNN
   answers with HTTP 200 even when something failed, so every line is read: an
   *Error!*, a package that failed or a portal that wasn't created fails the
   setup.
7. **Creating host account** - checks what DNN made (its version against the
   files, the host superuser, the portal alias) and finishes what DNN's wizard
   would: the host isn't asked to change its password at its first sign-in,
   and the pages aren't marked secure (the local site is http).
8. **Starting website** - the home page is requested once, so DNN finishes its
   start-up work now and not on your first visit; anything DNN logged as an
   error meanwhile is shown.

*DNN installation completed. Open http://… - sign in as '…'.* ends it, with a
notification and **Open site**. The install template (it holds the host
password), DNN's `Install.aspx`, `InstallWizard.aspx` and `UpgradeWizard.aspx`
and the `web.config` backups DNN made are then deleted - also when the setup
fails or is cancelled -, so nothing in the folder installs DNN again or holds
the password. The password is never in the Output tab, the log file or
`settings.json`. DNN Manager notes how the project was installed (and its host
username) in `Documents\DnnManager\projects\<project>.json`, which the
overview's **DNN** tab shows.

If DNN's installation fails, the project is left as it is to look into: remove
it (**Remove…**) and create it again - DNN can't install twice into the same
files and database.

**Manual DNN setup** creates the folder, the IIS website and the database as
before and leaves DNN's installation wizard for the first visit; the Output tab
says what to enter in it.

### Databases

The connection type is chosen once, in **Settings → Database server**; New project
names the database like the project and tests the connection before it creates
anything.

| Type | What the site uses |
|---|---|
| **Local SQL container (Docker)** | A database named like the project on the shared container, as `sa` - the default. |
| **SQL Server / SQL Server Express** | Any SQL Server, e.g. `.\SQLEXPRESS`: with *Windows authentication* the site signs in as its app pool identity, which DNN Manager makes a login and the database's owner (on this machine's SQL Server); with *SQL Server authentication* as a login that may create the database, or owns it. |
| **SQL Server Express LocalDB (file)** | The package's own `App_Data\Database.mdf`, run by LocalDB under the site's app pool identity. Fine for trying things out; DNN Manager can't open it while the site runs. |

**Create project** first tests the database and shows each check in the Output panel: the server answers, the
sign-in works, the version is new enough (SQL Server 2017 for DNN 10, 2012 for
DNN 9), and the database can be created - or, when it exists, is empty and
owned (`db_owner`) by the login; with Windows authentication, that the site's
login can be made. A database that already exists is only dropped after you
confirm it.

### Changing the host password

**Change host password…** on the overview's **DNN** tab sets a new password for
a host account (pick it, type the new one twice, or **Generate**), hashed the
way the site's membership provider stores it - DNN keeps only that hash, so the
current password can't be shown -, then restarts the site (a LocalDB file's
site is stopped while its database is changed). The new password isn't kept
anywhere.

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
   above them) and the Output tab shows a **⚠ warning** to switch them back on
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

**Clone…** on a project's right-click menu copies that DNN site (files +
database) into a new project under `BaseDirectory` with its own hostname, IIS
site and local database.

Flow ([`ProjectMenu`](src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs)
→ [`CloneProjectUseCase`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Name** - the new project's name (`<project>_copy` is suggested); one that
   isn't valid or is taken is refused as you type.
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

## Keep warm

A local DNN site is slow after a while without visits because IIS shuts its app
pool's worker process down after its **idle time-out** - 20 minutes by default.
The next request starts a new one, and ASP.NET and DNN start up again: 3 to 10
seconds instead of some 30 ms - and the first page after that takes almost a
second more. Recycles (every 29 hours by default, and DNN Manager's own: Restart,
Clear website cache…) and IIS starting do the same.

Click the **flame** in a site's row - or **Keep warm** on its right-click menu,
or the switch on its overview's **IIS** tab - and DNN Manager keeps the site warm
while it runs, minimized too:

- **Before IIS would shut it down**, it requests the site's keep-alive page -
  DNN's own `KeepAlive.aspx`, about 100 bytes, answered in a few milliseconds
  without database work - every 5 minutes (**Settings → Projects → Keep warm**),
  or sooner when the app pool's idle time-out needs it (at most 40% of it, but
  not more often than every 30 seconds). Not while the site is in use anyway -
  IIS's request counter went up since the last look - unless the idle time-out
  is so short (about a minute) that two intervals wouldn't fit in it. Also when
  the app pool never idles out: DNN itself still restarts after a rebuild, and
  that request starts it again.
- **As soon as its worker process is gone** - a recycle, a crash, the site or IIS
  started again - it warms the site up with its warm-up page, the home page (`/`):
  DNN starts and the page is compiled before you open it. A keep-alive request
  that comes back slowly (DNN had to start, after a rebuild say) gets a warm-up
  too. A site without a keep-alive page (it answers *404* twice in a row) gets
  its warm-up page instead.
- **No browser**, no process of its own: a plain request straight to the site on
  this PC (`127.0.0.1`, with the site's host name - whatever DNS says), as one
  anonymous visitor that keeps its cookies. Redirects are only followed to the
  same site, and never into DNN's installer.

It holds back while the site or IIS is stopped, while one of DNN Manager's
operations runs on the site (or on IIS itself), while a debugger is attached to
the site's worker process - Visual Studio's usual attach for .NET code too: a
request would stop at your breakpoints - and, for a warm-up, while the SQL
Server container the site's database is on doesn't answer. During other
operations (a new project, an import, a backup…) running sites are kept warm,
but none is warmed up from cold until the operation has ended; and nothing an
operation may have caused counts as a failure. Right after DNN Manager starts
it waits a minute before warming up sites without a worker process, and it warms
up one site at a time. A site that keeps failing is tried again after 1, 2 and 5
minutes, then left alone until you press **Check now**, start it again or the PC
wakes up - and so is one whose worker process keeps ending right after it was
warmed up (a crashing site). A keep-alive request that gets no answer in time is
followed by a warm-up, with its longer time limit (DNN may be starting slowly
after a rebuild) - or, while a debugger is attached, taken to be stopped at a
breakpoint: not a failure, and the next request only comes an interval later.
The first failure, giving up and recovering are noted in **Output**.

The flame shows how it is going: an outline while off; filled in orange while
the site is warm, pulsing while it warms up, grey while paused, with a red dot
while it fails - rest the mouse on it for the details. The overview's **Keep
warm** card shows the app pool's idle time-out, how often the site is requested
and which pages, and gives the site an interval and pages of its own.

Switching it on for a DNN site whose host settings send e-mail through a mail
server on another machine asks first - also when its database can't be read at
that moment: kept warm, DNN's scheduler keeps running too, and sends the e-mails
waiting in its queue - on a copy of a live site's database, to real people.

Which sites are kept warm is remembered in `Documents\DnnManager\projects\keep-warm`;
a site's file goes when the site is no longer in IIS (removed with **Remove…**,
or in IIS Manager - also while DNN Manager was closed). Keep warm changes
nothing in IIS: when DNN Manager is closed, the sites idle out as IIS has them
set up.

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
| **Use cases off the UI thread** | `OperationRunner` runs one use case at a time on the thread pool in its own DI scope, refuses a second one while it runs, and backs the status bar's **Cancel** button. A cancelled operation is undone through the scope's [`OperationUndo`](src/DnnManager.Application/UseCases/OperationUndo.cs): each step notes how to take back what it is about to make, before it starts. |
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

- [`ServerStateMonitor`](src/DnnManager.Infrastructure/Monitoring/ServerStateMonitor.cs)
  (background threads) is the only thing that reads the projects, IIS and this
  PC's figures. It keeps what it last read and raises one event with what
  differs: a project added, removed or changed - and which part of it: site,
  worker figures, database, size… -, IIS's state, the PC's figures, the
  connection.
- [`ServerStore`](src/DnnManager.Presentation/Services/ServerStore.cs) (UI
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
([`ChangeSources.cs`](src/DnnManager.Infrastructure/Monitoring/ChangeSources.cs));
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
[Efficiency mode](#efficiency-mode-while-the-window-cant-be-seen).

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
new page needs no styling of its own. [`App.xaml`](src/DnnManager.Presentation/App.xaml)
merges, in order: the colour palette (`LightTheme` / `DarkTheme`),
[`Tokens.xaml`](src/DnnManager.Presentation/Themes/Tokens.xaml) and the control
dictionaries in [`Themes/Controls/`](src/DnnManager.Presentation/Themes/Controls/).

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
  [`src/DnnManager.Installer/DnnManager.iss`](src/DnnManager.Installer/DnnManager.iss). Code signing can be
  added there (`SignTool`) and in `build.ps1`.
- **Add tests**: in `tests\DnnManager.IntegrationTests` - every use case takes
  pure interfaces, and the stand-ins are in its `Support\` folder
  (`UntouchedIis`, `TestPrompt` - which never says yes -, `RecordingReporter`,
  IIS Express as `IIisManager`…). A test that needs IIS Express, LocalDB or
  Docker gets `[TestCategory("Integration")]` and is inconclusive without them.

## Component map

| Area | C# location |
|---|---|
| Main window / navigation / activity log | [`MainWindow.xaml`](src/DnnManager.Presentation/MainWindow.xaml) |
| Status bar (IIS, resources, running operation, version) | [`Controls/StatusBar.xaml`](src/DnnManager.Presentation/Controls/StatusBar.xaml.cs), [`Controls/IisStatus.xaml`](src/DnnManager.Presentation/Controls/IisStatus.xaml.cs), [`UseCases/IisServerUseCase.cs`](src/DnnManager.Application/UseCases/IisServerUseCase.cs), [`Services/ServerStore.cs`](src/DnnManager.Presentation/Services/ServerStore.cs), [`Monitoring/HostResourceMonitor.cs`](src/DnnManager.Infrastructure/Monitoring/HostResourceMonitor.cs) |
| Live state of the projects, IIS and this PC (no Refresh) | [`Monitoring/ServerStateMonitor.cs`](src/DnnManager.Infrastructure/Monitoring/ServerStateMonitor.cs), [`Monitoring/ChangeSources.cs`](src/DnnManager.Infrastructure/Monitoring/ChangeSources.cs), [`Monitoring/MonitorModel.cs`](src/DnnManager.Infrastructure/Monitoring/MonitorModel.cs), [`Services/ServerStore.cs`](src/DnnManager.Presentation/Services/ServerStore.cs) |
| Projects table, site overview (portals), start / stop / restart, remove, export | [`ProjectsPage`](src/DnnManager.Presentation/Pages/ProjectsPage.xaml.cs), [`Pages/Projects/ProjectView`](src/DnnManager.Presentation/Pages/Projects/ProjectView.xaml.cs), [`Pages/Projects/`](src/DnnManager.Presentation/Pages/Projects/), [`Services/ServerStore.cs`](src/DnnManager.Presentation/Services/ServerStore.cs), [`Monitoring/ProcessSampler.cs`](src/DnnManager.Infrastructure/Monitoring/ProcessSampler.cs), [`UseCases/ControlSitesUseCase.cs`](src/DnnManager.Application/UseCases/ControlSitesUseCase.cs), [`UseCases/RemoveProjectUseCase.cs`](src/DnnManager.Application/UseCases/RemoveProjectUseCase.cs), [`UseCases/ExportProjectUseCase.cs`](src/DnnManager.Application/UseCases/ExportProjectUseCase.cs) |
| New project | [`SetupPage`](src/DnnManager.Presentation/Pages/SetupPage.xaml.cs) + [`UseCases/SetupProjectUseCase.cs`](src/DnnManager.Application/UseCases/SetupProjectUseCase.cs), [`UseCases/ImportProjectUseCase.cs`](src/DnnManager.Application/UseCases/ImportProjectUseCase.cs) |
| Automatic DNN setup (DNN's install, its output, the host account and password) | [`Dnn/DnnInstaller.cs`](src/DnnManager.Infrastructure/Dnn/DnnInstaller.cs), [`Dnn/DnnInstallTemplate.cs`](src/DnnManager.Infrastructure/Dnn/DnnInstallTemplate.cs), [`Dnn/DnnInstallOutput.cs`](src/DnnManager.Infrastructure/Dnn/DnnInstallOutput.cs), [`Dnn/MembershipPasswords.cs`](src/DnnManager.Infrastructure/Dnn/MembershipPasswords.cs), [`Abstractions/DnnInstall.cs`](src/DnnManager.Application/Abstractions/DnnInstall.cs), [`UseCases/ChangeHostPasswordUseCase.cs`](src/DnnManager.Application/UseCases/ChangeHostPasswordUseCase.cs), [`Controls/HostPasswordDialog.xaml`](src/DnnManager.Presentation/Controls/HostPasswordDialog.xaml.cs) |
| Databases: Test connection, create, the site's login, LocalDB files | [`Sql/DatabaseProvisioner.cs`](src/DnnManager.Infrastructure/Sql/DatabaseProvisioner.cs), [`Sql/LocalDbFiles.cs`](src/DnnManager.Infrastructure/Sql/LocalDbFiles.cs), [`Sql/ConnectionStrings.cs`](src/DnnManager.Infrastructure/Sql/ConnectionStrings.cs), [`Abstractions/Databases.cs`](src/DnnManager.Application/Abstractions/Databases.cs), [`Controls/DatabaseCheckList.xaml`](src/DnnManager.Presentation/Controls/DatabaseCheckList.xaml.cs) |
| Passwords in the Windows Credential Manager; how each project was installed | [`Settings/WindowsCredentialStore.cs`](src/DnnManager.Infrastructure/Settings/WindowsCredentialStore.cs), [`Projects/ProjectRecords.cs`](src/DnnManager.Infrastructure/Projects/ProjectRecords.cs) |
| Tests | [`tests/DnnManager.IntegrationTests/`](tests/DnnManager.IntegrationTests/) |
| Host project (IIS / DB) | [`ExistingFolderPage`](src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs) + [`UseCases/HostExistingProjectUseCase.cs`](src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs) |
| Shared IIS site / SQL container steps | [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |
| Clone a project | [`ProjectMenu`](src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs) (**Clone…**) + [`UseCases/CloneProjectUseCase.cs`](src/DnnManager.Application/UseCases/CloneProjectUseCase.cs) |
| SQL connection test | [`Sql/SqlConnectionTester.cs`](src/DnnManager.Infrastructure/Sql/SqlConnectionTester.cs) |
| Projects right-click menu / IDE detection | [`Pages/Projects/ProjectMenu.cs`](src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs), [`Services/IdeLocator.cs`](src/DnnManager.Presentation/Services/IdeLocator.cs) |
| Test and set up (Docker, database server, IIS features) | [`DockerCard`](src/DnnManager.Presentation/Controls/DockerCard.xaml.cs), [`DatabaseServerCard`](src/DnnManager.Presentation/Controls/DatabaseServerCard.xaml.cs), [`IisCard`](src/DnnManager.Presentation/Controls/IisCard.xaml.cs) + [`Prereq/WindowsPrerequisiteChecker.cs`](src/DnnManager.Infrastructure/Prereq/WindowsPrerequisiteChecker.cs) (checks and enables the IIS features - **Set up IIS**) |
| Settings page (Save applies at once) | [`SettingsPage`](src/DnnManager.Presentation/Pages/SettingsPage.xaml.cs), [`Services/LiveSettings.cs`](src/DnnManager.Presentation/Services/LiveSettings.cs), [`Configuration/AppOptions.cs`](src/DnnManager.Application/Configuration/AppOptions.cs) |
| settings.json: format, defaults, validation | [`Configuration/UserSettings.cs`](src/DnnManager.Application/Configuration/UserSettings.cs) |
| settings.json: load, save, backups, migrations | [`Settings/SettingsStore.cs`](src/DnnManager.Infrastructure/Settings/SettingsStore.cs), [`Settings/SettingsMigrations.cs`](src/DnnManager.Infrastructure/Settings/SettingsMigrations.cs), [`Settings/AppDataPaths.cs`](src/DnnManager.Infrastructure/Settings/AppDataPaths.cs) |
| Settings error dialog at startup | [`Services/SettingsStartup.cs`](src/DnnManager.Presentation/Services/SettingsStartup.cs) |
| Installer | [`src/DnnManager.Installer/DnnManager.iss`](src/DnnManager.Installer/DnnManager.iss), [`src/DnnManager.Installer/build.ps1`](src/DnnManager.Installer/build.ps1) |
| Bottom panel (Output, Logs, Terminal; search) | [`Controls/TerminalPanel.xaml`](src/DnnManager.Presentation/Controls/TerminalPanel.xaml.cs), [`Terminal/`](src/DnnManager.Presentation/Terminal/) (`TerminalBuffer`, `TerminalView`, `TerminalSession`), [`Services/TerminalService.cs`](src/DnnManager.Presentation/Services/TerminalService.cs), [`Terminal/PseudoConsole.cs`](src/DnnManager.Infrastructure/Terminal/PseudoConsole.cs) |
| Keep warm (the flame) | [`KeepWarm/KeepWarmService.cs`](src/DnnManager.Infrastructure/KeepWarm/KeepWarmService.cs), [`KeepWarm/KeepWarmRules.cs`](src/DnnManager.Infrastructure/KeepWarm/KeepWarmRules.cs) (why sites go cold, the numbers), [`KeepWarm/KeepWarmPlan.cs`](src/DnnManager.Infrastructure/KeepWarm/KeepWarmPlan.cs), [`KeepWarm/KeepWarmRequester.cs`](src/DnnManager.Infrastructure/KeepWarm/KeepWarmRequester.cs), [`Projects/KeepWarmRecords.cs`](src/DnnManager.Infrastructure/Projects/KeepWarmRecords.cs), [`Services/ServerStore.cs`](src/DnnManager.Presentation/Services/ServerStore.cs) |
| Site tools: clear cache, the Logs tab | [`UseCases/ClearSiteCacheUseCase.cs`](src/DnnManager.Application/UseCases/ClearSiteCacheUseCase.cs), [`SiteLogs/SiteLogs.cs`](src/DnnManager.Infrastructure/SiteLogs/SiteLogs.cs), [`Controls/LogsView.xaml`](src/DnnManager.Presentation/Controls/LogsView.xaml.cs), [`Controls/PanelSearch.cs`](src/DnnManager.Presentation/Controls/PanelSearch.cs) |
| Start at sign-in | [`Startup/StartupTask.cs`](src/DnnManager.Infrastructure/Startup/StartupTask.cs) |
| Efficiency mode while out of sight | [`Services/WindowOcclusion.cs`](src/DnnManager.Presentation/Services/WindowOcclusion.cs), [`Services/EfficiencyMode.cs`](src/DnnManager.Presentation/Services/EfficiencyMode.cs), [`Processes/PowerThrottling.cs`](src/DnnManager.Infrastructure/Processes/PowerThrottling.cs) (EcoQoS) |
| Themes | [`Themes/`](src/DnnManager.Presentation/Themes/), [`Services/ThemeManager.cs`](src/DnnManager.Presentation/Services/ThemeManager.cs) |
| Control styles (buttons, inputs, selects, switches…) | [`Themes/Controls/`](src/DnnManager.Presentation/Themes/Controls/), [`Themes/Tokens.xaml`](src/DnnManager.Presentation/Themes/Tokens.xaml) |
| File copy, zip extract / create | [`Files/ProjectFileCopier.cs`](src/DnnManager.Infrastructure/Files/ProjectFileCopier.cs) |
| GitHub release lookup (releases and pre-releases, the latest release by default) | [`Github/GitHubDnnReleaseService.cs`](src/DnnManager.Infrastructure/Github/GitHubDnnReleaseService.cs), [`Services/DnnReleaseCatalog.cs`](src/DnnManager.Presentation/Services/DnnReleaseCatalog.cs) |
| IIS helpers | [`Iis/IisManager.cs`](src/DnnManager.Infrastructure/Iis/IisManager.cs) |
| sqlcmd | [`Sql/SqlServerService.cs`](src/DnnManager.Infrastructure/Sql/SqlServerService.cs) |
| Shared SQL container | `docker-compose.yml` made from the settings by [`Docker/DockerComposeService.cs`](src/DnnManager.Infrastructure/Docker/DockerComposeService.cs) and run by [`UseCases/SetupSqlContainerUseCase.cs`](src/DnnManager.Application/UseCases/SetupSqlContainerUseCase.cs) (Settings → Docker container → Set up docker-compose); the connection check is `LocalSqlContainer` in [`UseCases/Provisioning.cs`](src/DnnManager.Application/UseCases/Provisioning.cs) |

## Notes / limitations

- The dark title bar needs Windows 10 20H1 or later; older versions keep a light one.
- File pickers (Browse… / Save as) are standard Windows dialogs and follow the
  Windows theme, not the app's. Questions and warnings use the app's own dialog.
- The app runs as Administrator, so an IDE opened from the project menu does too
  (VS Code shows *[Administrator]* in its title).
- The SQL Server keeps the sa password its data volume was created with -
  changing **SA password** in Settings doesn't change it in an existing
  container.
- Automatic DNN setup is tested with DNN 10.3.3 on IIS Express, with LocalDB and
  a SQL Server 2022 container (DNN's unattended install itself also with DNN
  9.13.10). On IIS the site signs in to SQL Server as `IIS APPPOOL\<project>`,
  which only works for a SQL Server on this machine - for one on another
  machine, use SQL Server authentication. A language other than English
  (`en-US`) makes DNN download its language pack while installing.
- [Keep warm](#keep-warm) only works while DNN Manager runs. A site that
  restarts without a new worker process - a rebuild changed `bin` or
  `web.config` - is warmed by its next keep-alive request, within the interval.
- Cloning from a SQL Server on another machine (not Azure SQL) writes the
  `.bak` on that server, in this PC's temp path, so it only works when the source
  server runs on this machine. Azure SQL sources go through a `.bacpac` and work
  from anywhere.

## Third-party

- **Codicons** - the terminal list's shell icons (*terminal-powershell*,
  *terminal-cmd*, *terminal-bash*, drawn from their SVG paths in
  `Controls/TerminalPanel.xaml`) and keep warm's flame (*flame*, in
  `Themes/Controls/ButtonStyles.xaml`) are from
  [Codicons](https://github.com/microsoft/vscode-codicons) by Microsoft,
  licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
