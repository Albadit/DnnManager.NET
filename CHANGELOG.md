# Changelog

All notable changes to DnnManager.NET are documented here.

## v2.3.0 - 2026-10-01

**New project installs DNN for you**: the first visit shows your new site, signed
in as the host account you chose - no installation wizard - on the local SQL
container, SQL Server / SQL Server Express or a LocalDB file, tested before
anything is created. The bottom panel works like VS Code's (Output, Logs,
Terminal, search), a site opens in an overview with tabs, and the app is light
on resources while minimized.

### Upgrading from v2.2.x

- Install over v2.2.x as usual - settings are kept; the new keys
  (`projects.dnnDefaults`, `projects.databaseProfiles`,
  `projects.defaultDatabaseProfile`, `window.saveResourcesWhileMinimized`) are
  added with their defaults on the first start.
- The program is now `DnnManager.exe`. Shortcuts made by Setup are updated; a
  shortcut or script of your own that names `dnnmanager.exe` still works
  (Windows file names ignore case).

### Changed

- **The program is `DnnManager.exe`** (was `dnnmanager.exe`); Setup replaces the
  old file when it upgrades.
- **DNN defaults** start as host `host` / `Admin@123`, `admin@admin.com`,
  website *My Website*, English, *Default Website* - the password while none
  is saved in the Windows Credential Manager.
- **Projects shows IIS, not the projects folder.** Every DNN website configured
  in IIS is a row - also one serving a folder elsewhere; sites without DNN (such
  as IIS's own *Default Web Site*) aren't listed -, and what the row shows comes
  from IIS: name, state, app pool,
  bindings (host names, ports, protocols, SSL certificate), physical path. The
  DNN version and the database are read from the folder the site serves. A
  folder in the projects folder without an IIS site is no longer a row (set it
  up with **Host project**). The site's address is its https binding when it
  has a certificate, else its http one.
- **Remove…** deletes files only for a site in the projects folder; for a site
  whose folder is elsewhere only its IIS site goes (and the database its
  web.config names, if you say so).

- **The bottom panel works like VS Code's** - three tabs in its header:
  **Output** (what DNN Manager does - was *Activity*), **Logs** (new) and
  **Terminal**, which now holds only shells, listed on the right - each with
  its shell's icon (PowerShell, Command Prompt, Bash), also in the shell menu;
  the list can be resized by dragging its edge, and a terminal's bin shows on
  hover. Opening the Terminal tab with no shell open starts one. **Ctrl+`** shows
  or hides the panel.
- **The site overview has tabs** - **General**, **IIS**, **DNN**, **Database**
  and **Advanced**, one shown at a time. **DNN** shows how DNN was installed,
  its version, the host account (username and e-mail - never a password) and
  the first portal's name and alias, then the portals; **Database** the
  connection type, server, database and authentication, with **Test
  connection**; **Advanced** the web.config.
- **New project** sets the site's **host name and port** (they follow the
  project's name until you type your own), and the Output tab names each step:
  *Testing database connection*, *Creating project directory*, *Creating IIS
  application pool and website*, *Creating database*, *Configuring DNN*,
  *Running DNN installation*, *Creating portal*, *Creating host account*,
  *Starting website*, *DNN installation completed*. A database that already
  exists is only dropped after you confirm it.
- **Remove…** drops a project's database where it is: on the local container,
  on another SQL Server (through the site's own connection), or - a LocalDB
  file - with the folder.
- The Projects table's **SQL** column says *External* for a site whose database
  isn't on the local SQL container.

### Added

- **Automatic DNN setup** - **New project** installs DNN for you, so the first
  visit shows the new site instead of DNN's installation wizard: DNN's own
  unattended install (`Install.aspx?mode=install`) with the host account,
  website name, language and site template you chose, its progress in the
  Output tab, every line of it checked (DNN reports success even when a
  package or the portal failed). Then the host signs in without being asked
  to change the password, and the install template - which holds the password
  - and DNN's installer pages are deleted. Checked first, before anything is
  created: the host password (7 to 128 characters, no `<` or `&#` - DNN fails
  silently or its login form refuses them), the folder's depth, the database.
  **Manual DNN setup** is still there and leaves DNN's wizard for the first
  visit.
- **Database choice** for a new project: the local SQL container (as before),
  **SQL Server / SQL Server Express** with Windows or SQL Server
  authentication (with Windows authentication the site's app pool identity is
  made a login and the database's owner), or a **LocalDB database file** (the
  package's own `App_Data\Database.mdf`). **Test connection** shows each
  check - server reachable, signed in, version, can create (or owns) the
  database, the site's login - and **Save as profile…** keeps the connection.
- **DNN defaults** - **Settings → Projects**: install mode, host username and
  password, e-mail, website name, language and site template that each new
  project starts with; and the **database profiles**, with the one new projects
  use. The passwords are kept in the Windows Credential Manager, not in
  `settings.json` (new keys `projects.dnnDefaults`, `projects.databaseProfiles`,
  `projects.defaultDatabaseProfile`).
- **Change host password…** - on the overview's **DNN** tab: a new password for
  a host account, stored the way DNN's membership provider does, then the site
  restarts. The current password is never shown (DNN keeps only a hash).
- **Tests** - `tests\DnnManager.IntegrationTests` (MSTest): fast tests of the
  install's parts and of what New project refuses, and integration tests that
  create projects with automatic setup on a clean DNN 10.3.3 - IIS Express for
  IIS, with LocalDB (Windows authentication and a database file) and a SQL
  Server container (as sa and as a login of its own) - and check the site, the
  host's sign-in, the database, a restart and changing the password; and that
  manual setup leaves DNN's wizard. See *Tests* in the README.

- **Site tools** - on a site's right-click menu (and a **⋮** on its overview):
  **Clear website cache…** (DNN's cached files and bundled
  CSS / JavaScript, then an app pool recycle - asked first, with a notification
  when done) and **View logs** ▸ with the site's logs.
- **Logs tab** - a website's DNN logs, IIS request logs, HTTP.sys errors and
  the Windows events about it (ASP.NET, its app pool, worker process crashes),
  chosen at the top - any IIS site, right there. Only the end of a large file is
  read; new lines and events appear as they are written. Its text can be
  selected across lines and copied.
- **Search** - **Ctrl+F** in the bottom panel searches Output, Logs or the
  terminal: highlights, *2 / 14*, next / previous (**Enter**, **Shift+Enter**,
  **F3**), Match Case / Match Whole Word / Use Regular Expression (**Alt+C**,
  **Alt+W**, **Alt+R**), following new output; closing it leaves the text as it
  was.
- **Maximize the panel** - its button (or **Ctrl+Shift+M**) gives it the page's
  room, its tabs still there; again restores it.
- **Site overview** - click a site's name (or double-click its row, or press
  Enter) for an overview in place of the table: state and Start / Stop /
  Restart, the IIS website and its app pool, its bindings as links, and the
  folder's, database's and web.config's facts. Replaces the *Details…* window.
- **DNN portals** - for a DNN site the overview lists every portal of its
  installation, from its database (one IIS site can serve several): ID, name,
  status, its primary alias as a link that opens in the browser - https when the
  site serves that host over https - and its other aliases.
- **Efficiency mode while minimized** - with the window minimized, DNN Manager
  stops what only the window shows: the progress bars and a changing site's
  pulsing dot (WPF kept drawing about 60 frames a second for them, unseen),
  this PC's figures in the status bar, drawing terminals (their output is still
  read), following a log file (read on at once when restored - no line is lost)
  and folder-size walks; a toast that comes meanwhile waits to be seen. Once
  nothing runs - no operation, no terminal printing - Windows is asked to run
  it power-efficiently (EcoQoS); an operation always runs at full speed. Sites
  are still followed (Windows' notifications, the reconciliation every 30
  seconds), and restoring the window brings everything up to date at once,
  without a loading screen. Minimized and idle, the app now uses about a
  seventh of the processor cycles it did, and during an operation the progress
  bars stand still (new lines on an open Output tab are still drawn).
  On by default: **Settings → General → Save resources while minimized**
  (`window.saveResourcesWhileMinimized`).

### Fixed

- A changing site's pulsing status dot no longer runs while the Projects page
  isn't shown.

### Security

- No password in the Output tab, the log file or an error message: a
  connection string that couldn't be written is no longer quoted in the error,
  and database connections print without their password.

## v2.2.0 - 2026-10-01

DNN Manager now works like Docker Desktop: **Projects** is a live server table
that follows IIS and the projects folder by itself - no Refresh -, a status bar
shows IIS and this PC's resources, and a terminal panel holds the activity log
next to real terminals. Settings is a page of its own and applies without a
restart, and every button, input and switch shares one look.

### Upgrading from v2.1.x

- `settings.json` is upgraded on the first start (its `version` becomes 2, the
  Docker values move to a `docker` section); the old file is backed up to
  `Documents\DnnManager\backups\` first. The SA password in it is encrypted on
  that start too.
- The program is now `dnnmanager.exe` and the environment variables start with
  `DNNMANAGER_` (was `DNNMGR_`) - re-pin the app if it was pinned to the taskbar.
- The SQL Server container's compose project is renamed from `dnn-shared` to
  `dnn-mssql`. Press **Environment → Set up docker-compose** once: it removes the
  old container and makes it again under the new name. The databases are kept -
  they live in the data volume, which is reused.

### Fixed

- **Imported and cloned sites no longer redirect to https.** A database from a
  live site often has DNN's SSL on (`SSLSetup` in DNN 10, `SSLEnabled` /
  `SSLEnforced` in DNN 9, or pages marked secure), so DNN sent every
  `http://<project>.dnndev.me` request to `https://`, which the local site
  doesn't answer. Host project (with a backup restored), Import and Clone now
  switch it off in the local database, with a warning in the activity log to
  switch it back on before that database goes live again.
- **Setup closes after uninstalling.** Choosing *Uninstall DNN Manager* in
  Setup now closes Setup once the uninstall is done, instead of leaving it open
  on the same page.

### Added

- **Toasts.** Short messages over the bottom-right of the page - e.g. why the
  settings couldn't be saved - seen wherever the page is scrolled.
- **Refresh buttons** next to the DNN version list (New project) and the
  project folders (Host project).

- **Pick a DNN version from a list.** On **New project**, choose the
  **Repository** (shown as `owner/repo`), then a **Version** from its GitHub
  releases - newest first, with the latest release selected. Pre-releases are
  listed too, marked *(pre-release)*, but are never selected by default. Before,
  the version had to be typed (blank for the latest).
- **Keep downloaded DNN packages.** A new setting, **Keep downloaded DNN install
  packages for next time** (`projects.keepDnnPackages`, off by default), keeps
  each downloaded `DNN_Platform_<version>_Install.zip` in
  `Documents\DnnManager\packages\<owner>.<repo>\` and uses it again when a new
  project picks the same version, without downloading. The version list marks
  those versions *kept, no download*. Downloads now show their progress.

- **Start, stop and restart sites.** Each row on **Projects** has the actions
  for its IIS site's state - **Stop** and **Restart** (recycles the app pool)
  while it runs, **Start** while it's stopped - next to **Remove…**. The right-click
  menu has them too.
- **Status bar** along the bottom of the window, laid out like Docker
  Desktop's: under the sidebar whether IIS runs, with **Start** / **Stop** /
  **Restart** (`iisreset`); then this PC's RAM, CPU (two decimals) and disk use
  (the projects folder's drive, with its size as the limit) - each figure keeps
  its room, so they don't jump as the numbers change -, the running operation
  with **Cancel**, a button that opens the activity log, and the app's version
  (moved from the sidebar).
- **Compact sidebar** - in a window narrower than 1100 pixels the sidebar shows
  only the page icons (names as tooltips), and IIS in the status bar only its
  dot with a **⋮** menu.
- **Terminal.** The activity panel is now a terminal panel: the **Activity**
  tab keeps the log of what DNN Manager does, and **+** opens real terminals
  next to it - **PowerShell** by default, or **PowerShell 7**, **Command
  Prompt** or **Git Bash** (when installed) from the arrow beside it - as many
  as you like, listed on the right. **Open in terminal** on a project's
  right-click menu opens one in its folder. They run in Windows' pseudo console,
  so colours, tab completion and full-screen programs work; a scrollbar (or the
  wheel) scrolls back and a **↓** button returns to the newest line, drag selects, Ctrl+C / Ctrl+V or a right-click copy and paste. Resting
  the mouse on a tab shows its shell, process ID, program and folder; a
  double-click (or F2) renames it, and its bin ends it.
  **Settings → General** has the default shell, the font family and size, and
  a switch to turn terminals off (`terminal.*` in `settings.json`).
- **Start DNN Manager when you sign in** (Settings → General) - through a
  scheduled task with its Administrator rights, so there is no UAC question at
  sign-in. Uninstalling removes the task.
- **Own title bar** with **Settings** next to the window buttons (moved from
  the bottom of the sidebar). Snap layouts still show on Windows 11's maximize
  button. The sun / moon theme button is gone - the theme is chosen in
  **Settings → General**.
- **Search boxes** have a **✕** that empties them.
- **DNN Manager runs once.** Starting it again - Start menu, desktop shortcut,
  the exe - brings the open window to the front (restored if it was minimized)
  instead of opening a second copy, and without asking for Administrator rights
  again.

### Changed

- **Settings apply without a restart.** **Save and restart** is now **Save**:
  it checks the values, writes `settings.json` and puts the settings to work at
  once - another projects folder shows its projects in the table, a new site
  address, SQL Server or container name is what the next operation uses. The
  *Restart now* banner is gone. The **General** settings (theme, terminal,
  start at sign-in) are saved with the same button now, instead of applying by
  themselves while **Save** stayed greyed out.
- **Only show running** - a switch next to the Columns button on **Projects**
  that leaves the projects whose site is running.
- The status bar's terminal button is now **>_ Terminal**.
- **Bulk actions as one group of icons** - with rows checked, **Remove…** (on
  the left, in red), **Start**, **Stop** and **Restart** show as icons in one
  frame instead of four buttons with text; their tooltips say what each will do.
- The sidebar slides between its wide and narrow width, and the on / off
  switches slide when switched.
- **Copy path** is gone from the project's right-click menu (the path is in
  the row's details and in the *Path* column).
- **Projects' default columns** are the ones for daily work: DNN version,
  Database, SQL, CPU, Memory, PID (to attach a debugger) and Last started.
  **Default** in the Columns menu goes back to them; a choice you already made
  stays until you press it. The Columns menu and the table list the
  columns in that order - the most used first.
- Check boxes and radio buttons (Settings, New project, Host project) follow the
  theme instead of Windows' white ones.
- **Projects keeps itself up to date - its Refresh button is gone.** Like
  Docker Desktop's list, the table follows the system: a site started or
  stopped in IIS Manager, IIS restarted, a worker process that ended, a project
  folder made or deleted in Explorer - each shows up by itself, in the row it
  is about, within seconds. The rest of the table isn't touched: the
  checked rows, the search, the sort order, the scroll position and the open
  details stay as they are, and nothing "loads" after the first time.
  **Start**, **Stop** and **Restart** show *Starting…* / *Stopping…* /
  *Restarting…* in the row at once (**Remove…** shows *Removing…* once you
  have confirmed), then what IIS reports; when one fails, the row goes back to
  its real state and a toast says why (**Show activity** opens the log). A
  stopped site stays *Stopping…* until its worker process has ended, and
  **Start** waits for that. A site whose app pool is stopped shows as stopped
  (*App pool stopped*) - **Start** starts the pool. Only while IIS's
  configuration or the projects folder can't be read, or after the PC wakes
  up, *Reconnecting…* shows top right - the table stays and catches up by
  itself. Changes made outside
  DNN Manager are noted in the activity log. IIS's state in the status bar is
  now reported by Windows when it changes, instead of being asked for every 2
  seconds. How it works is in the README under *Live updates*.
- **Projects is a server table.** Check rows (or the header box for every row
  shown) to **Start**, **Stop**, **Restart** or **Remove** them together -
  those buttons appear above the table only while rows are checked, and each
  acts on the checked rows it applies to. Removing several asks once for all of
  them. A **search** box filters as you type (name, site, ID, port, status, DNN
  version, database, path), and the **Columns** button switches columns on and
  off - saved as `appearance.projectColumns`. New columns show each site's live
  state (a status dot), IIS site ID, ports, its network I/O (HTTP bytes received
  / sent) and its worker process's CPU, memory, disk read/write, PID and start
  time. A chevron opens a row's details under
  it. **Actions** stays at the right edge while the other columns scroll
  sideways, so a narrow window still shows them. The counts sit under the table
  (*4 of 12 projects*, *Selected 2 of 12*); **Open site** is a double-click on
  the row, and the separate **Open site** / **Remove…** buttons are gone.
- **Settings is a page of its own**, laid out like Docker Desktop's: the app's
  sidebar makes way for the settings' categories - **General** (new: start at
  sign-in, the theme as *Light* / *Dark* / *Use system settings*, the terminal,
  and the settings file),
  **Projects**, **DNN releases**, **SQL Server**, **Docker container**, **IIS**
  (new: the required Windows features) and **About** (new: the version and your
  folders) - with a search box over them. **Close** goes back to the page you
  came from; **Save** is at the bottom right.
- The sidebar no longer repeats the app's name - it is in the title bar.
- **Activity log** - now the first tab of the terminal panel. Closed, the panel
  takes no room: the running operation, its progress and **Cancel** moved to the
  status bar, whose terminal button opens the panel (a click on the running
  operation opens it on Activity).
- **Environment: Set up IIS.** The IIS card has a **Set up IIS** button under
  its feature table, like the Docker card's **Set up docker-compose** - always
  there, not only after a Test found missing features. It checks the Windows
  features and enables the missing ones after asking. **Reset IIS** and
  **Enable missing features** are gone: restarting IIS is on the status bar.
- **Docker compose project renamed** from `dnn-shared` to `dnn-mssql` (see
  *Upgrading from v2.1.x*).
- **One look for every control.** Text boxes and password boxes now have
  rounded corners like the buttons, selects and search boxes; buttons, text
  boxes and selects share one height (30 px) so they line up side by side; the
  right-click menus and select lists are rounded too, and a select shows the
  accent colour when it has the keyboard. A select with nothing to choose from
  (e.g. Clone's source project when the projects folder is empty) is greyed
  out. The styles are reusable: one file per
  kind of control in `Themes/Controls`, sharing sizes from `Themes/Tokens.xaml`
  (see *Control styles* in the README).
- **Open with: one submenu with your editors.** The project menu's separate
  *Open in …* entries are now one **Open with** submenu listing only the editors
  installed on the PC. Newly found: IntelliJ IDEA, Zed, Vim and Neovim (their
  windowed versions, or the console ones in a window of their own), next to
  Visual Studio, VS Code (and Insiders, Cursor, Windsurf), Rider and Sublime
  Text. The SQL Server Management Studio entries read **Open with SQL Server
  Management Studio <version>**.
- **SA password encrypted.** `sqlServer.saPassword` in `settings.json` is now
  stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - it
  can't be hashed, as DNN Manager needs the password itself to sign in to SQL
  Server. A plain password in the file is encrypted on the next start, so it can
  still be changed by hand. The activity log no longer prints it, and
  **Show docker-compose.yml** leaves it out: the file has a
  `<your-sa-password>` placeholder to replace after copying, and its health
  check reads the password from the container's own environment. Sites' `web.config` still holds it -
  DNN needs it there to connect.
- **Renamed from dnnmgr to dnnmanager.** The program is now `dnnmanager.exe`,
  the log files are `logs\dnnmanager-<date>.log`, and the environment variables
  that override settings start with `DNNMANAGER_` (e.g.
  `DNNMANAGER_DnnManager__Docker__SaPassword`) instead of `DNNMGR_`. The Start
  menu and desktop shortcuts point at the new exe (re-pin it if it was pinned to
  the taskbar). Old log files are cleaned up after 30 days as before.
- **Activity log starts collapsed.** Only its header bar shows at first - with
  the running operation, its progress and Cancel; the chevron opens the log.
- **New project only creates new projects.** A name whose folder already exists
  now shows *A project named '…' already exists* and can't be set up, instead
  of offering the existing-folder choices (and downloading DNN over the
  folder). Setting up an existing folder is what **Host project** is for. After
  a project is created, the name is cleared for the next one.

- **Settings: Save instead of autosave.** Edits on the Settings page are no
  longer saved as you type. Once something changes, **Save** (bottom right)
  checks the values, saves them and applies them, next to **Discard changes**.
  Leaving the page or closing the app with unsaved changes asks first.

- **Faster pages.** Pages are kept while the app runs instead of being rebuilt
  on every visit, so the Host project folders load once; **Refresh**, or a
  finished operation, loads them again. The DNN versions are asked of GitHub
  once, in the background when the app starts.
- **DNN versions sorted by version number,** highest first - GitHub lists them
  by date, which put e.g. 9.13.10 between 10.2.0 and 10.1.2. The "latest"
  release is now the highest version, too.

- **Docker and SQL Server settings apart.** The Settings page has a **SQL
  Server** card (host, port, SA password, database name suffix) and a **Docker
  container** card (container name, volume, edition, collation). In
  `settings.json` the container values move from `sqlServer` to a new `docker`
  section - the file's `version` becomes 2, and an existing file is backed up to
  `backups\` and upgraded on the next start.
- **Environment: Docker and SQL Server apart.** The *Docker and SQL Server*
  card is split into a **Docker** card (Docker Desktop, engine, SQL Server
  container) and a **SQL Server** card (the connection), each with its own
  **Test**.
- **Set up docker-compose, without a docker-compose.yml file.** The Docker card's
  **Set up docker-compose** button (replacing **Set up container** / **Start
  container**) runs the docker-compose.yml made from the settings - handed to
  `docker compose up -d` directly, so no file is written and it can't get out
  of step with the settings. It creates the container, starts it, or updates it
  after the settings changed, and waits for the sa login. **Show
  docker-compose.yml** shows the same file with a **Copy** button (without the
  SA password). The docker-compose.yml in `Documents\DnnManager` (or next to an
  old DNN Manager exe) is no longer used.
- **Databases named like the project.** A new project's local database is now
  named like the project (`ceesboer`, not `ceesboer_dnndev`), and the *Database
  name suffix* setting is removed - `sqlServer.databaseNameSuffix` is dropped
  from `settings.json` when it is upgraded. Existing sites keep their database:
  DNN Manager uses the one their `web.config` names. A project can't be named
  after a SQL Server system database (`master`, `model`, `msdb`, `tempdb`).

## v2.1.0 - 2026-09-29

DNN Manager now has a Windows installer, and your settings and project backups
move into `Documents\DnnManager`, apart from the program and the sites, where
updates, reinstalls and uninstalls leave them alone.

### Upgrading from v2.0.x

- Settings are now in `Documents\DnnManager\settings.json`, in a new format
  (grouped, camelCase, with a `version`). Start the new `dnnmgr.exe` once from
  the folder of the old one and its `appsettings.json` and `docker-compose.yml`
  are carried over (the originals are left in place). If you install the new
  version elsewhere, copy the old `appsettings.json` to
  `Documents\DnnManager\settings.json` before the first start - it is
  converted automatically.
- `DNNMGR_DnnManager__*` environment variables still override settings, with
  the same names.
- The `Logging` section of `appsettings.json` is gone (it had no effect).
- Project backups are no longer kept in each project's `01_backup` folder. Move
  the dated backup folders you want to keep from `<project>\01_backup\` to
  `Documents\DnnManager\backups\<project>\`, then delete `01_backup` - DNN
  Manager doesn't read it anymore, and a site export now includes it.

### Added

- **Windows installer** (`DnnManagerSetup-<version>-x64.exe`, built with
  `installer\build.ps1` and Inno Setup). It works like the VS Code user
  installer: no administrator rights, installs into
  `%LOCALAPPDATA%\Programs\DnnManager` (or a folder you pick), adds a Start
  menu shortcut and an optional desktop shortcut, registers in **Installed
  apps** for uninstalling, and can start the app when it finishes. When DNN
  Manager is already installed, Setup first offers **Repair** (in place, same
  folder) or **Uninstall**; Setup and the uninstaller ask you to close DNN Manager when
  it's running. `/ALLUSERS` installs for all users into Program Files.
- **Settings and backups in Documents.** `Documents\DnnManager` holds
  `settings.json`, `docker-compose.yml`, `backups\` and `logs\`, and is created
  on first start.
- **Checked settings.** At startup, invalid JSON, a value of the wrong type or a
  value that isn't allowed (e.g. a port above 65535) opens a dialog that says
  what's wrong - **Try again**, **Open file**, **Reset to defaults** (keeping
  the old file in `backups\`) or **Exit** - instead of the app failing. Missing
  keys are filled in with their defaults.
- **Versioned settings.** `settings.json` has a `version`; a file in an older
  format is backed up to `backups\` and upgraded, and a file from a newer DNN
  Manager is refused rather than overwritten.
- **`_backup.filter`.** Exporting a site reads the project's `_backup.filter`
  (the Azure App Service backup format, e.g. `\site\wwwroot\App_Data\Search`)
  and leaves out every file and folder it lists - caches, search indexes, logs.
  The activity log shows what was left out.
- **Log files.** The activity log is also written to
  `Documents\DnnManager\logs\dnnmgr-<date>.log` (kept 30 days).

### Changed

- The **Settings** page saves each change automatically once it is valid (the
  Save and Revert buttons are gone), and has **Open settings.json** and **Open
  settings folder**.
- `docker-compose.yml` is written to `Documents\DnnManager` instead of next to
  the exe.
- **Project backups in one place.** Export and Clone write backups to
  `Documents\DnnManager\backups\<project>\<project>_<date>\` instead of the
  project's `01_backup` folder, so they are kept when a project is removed.
  **New project → From a project backup** also lists removed projects' backups,
  and the project menu's **Open 01_backup folder** is now **Open backups
  folder**. **Export to another folder…** is removed - every export goes to the
  backups folder (copy it from there with **Open backups folder**). The `web.config` that blocked IIS from serving `01_backup` is no
  longer needed.

## v2.0.0 - 2026-09-28

A major release: projects can be imported from and exported to a `.zip` +
`.bacpac`, with dated backups in each project's `01_backup` folder; a new
**Environment** page checks and sets up Docker, the SQL Server container and
IIS; the Projects table gets live status columns and a right-click menu
(details, IDEs, SQL Server Management Studio, export); and the app is faster and
fully themed. FTP and the Live sites page are removed, and the app checks the
SQL Server connection instead of Docker.

### Upgrading from v1.0.x

- `connections.json` (saved FTP / SQL connections of live sites) is no longer
  used and can be deleted.
- DNN Manager no longer starts the SQL container when a project needs it. If it
  isn't running, start it on the **Environment** page (**Start Docker Desktop**,
  **Set up container** / **Start container**) - setting it up also rewrites
  `docker-compose.yml` next to the app from the settings, without the old fixed
  `dnn_network` subnet. An existing data volume keeps its data and its sa
  password.
- New backups go to `<project>\01_backup\`. An existing `backups` folder is not
  moved - it is still read when restoring.
- The SQL Server host now defaults to `localhost`. An existing `appsettings.json`
  keeps its value (e.g. `127.0.0.1`); both work.

### Added

- **Environment page** (replaces *Prerequisites*). Shows whether Docker Desktop,
  the Docker engine, the SQL Server container, the SQL Server connection and
  each IIS Windows feature are active - green / red - and fixes what isn't:
  **Install Docker Desktop** (winget), **Start Docker Desktop** (then waits for
  the engine), **Set up container** / **Start container** (writes
  `docker-compose.yml` from the SQL Server settings, runs `docker compose up -d`
  with live progress and waits for the sa login), **Show docker-compose.yml**,
  **Enable missing features** and **Reset IIS** (`iisreset`, after a
  confirmation). Each card has its own **Test** button - nothing is checked just
  by opening the page - and an action re-tests its card.
- **Import a site .zip as a new project.** New project has a **Start from**
  choice: a new site (download a DNN release, as before) or an existing site -
  a `.zip` of its files plus its database `.bacpac` (a `.bak` works too), picked
  **From a project backup** (a project and one of its dated backups) or from
  anywhere on the PC. The zip is extracted into the new folder (the site root is
  found by its `web.config`), then the site is hosted like Host project: IIS
  website, restored database, portal alias and `web.config`.
- **Dated backups in `01_backup`.** A project's backups live in
  `<project>\01_backup\<project>_<yyyyMMdd_HHmmss>\` with `<project>.zip` (site)
  and / or `<project>.bacpac` (database). The project menu's **Export** writes
  such a backup (site and database, site files or database), **Export to another
  folder…** saves a `.zip` + `.bacpac` anywhere, and **Open 01_backup folder**
  opens it. Clone keeps its source backup there too. The site zip leaves
  `01_backup`, the old `backups` folder and `.git` out (files the running site
  holds open are read too), and a `web.config` in `01_backup` makes IIS refuse
  to serve the backups.
- **Project right-click menu.** Right-click a project on the Projects page for
  **Details…**, **Open site**, **Open folder**, **Copy path**, **Open in …** for
  each IDE installed on the PC (Visual Studio via `vswhere`, opening the
  project's `.sln` when there is exactly one; VS Code, VS Code Insiders, Cursor,
  Windsurf, Rider, Sublime Text), **Open in SQL Server Management Studio** ▸,
  **Export** ▸ and **Remove…**. A right-click selects the row under the mouse.
- **Project details.** **Details…** shows the project in sections - Project
  (folder, created, size, DNN version, git branch, solution, backups), Website
  (Live / Offline, URL, bindings, physical path, app pool, .NET version,
  pipeline, identity), Database (Live / Offline, size, the DNN version recorded
  in it, portals, portal aliases) and web.config (connection, target framework,
  debug, custom errors, switched-off HTTPS redirects). A folder that doesn't
  match the IIS path, a missing database or a DNN version that differs between
  files and database is shown in amber. Everything is selectable, with **Copy all**.
- **Open a project's database in SQL Server Management Studio.** One submenu per
  installed SSMS (21 and later via `vswhere`, 18-20 by their install folders):
  *Default* signs in to the local SQL Server as `sa`, *Project* to the database
  the site uses. For SSMS 21+ DNN Manager fills in its Connect dialog (server,
  SQL Server Authentication, login, password, database, trust server
  certificate, name) and connects - SSMS takes no password on its command line.
  An SSMS that's already open gets the new connection instead of a new window.
  A missing database opens the server instead, and the Activity log says so.
  SSMS 18-20 get command-line switches and the password on the clipboard.
- **Remember the password in SQL Server Management Studio** - a setting
  (`DnnManager:SsmsRememberPassword`, off by default) that ticks SSMS's
  *Remember Password* when DNN Manager signs it in.
- **HTTPS redirects are switched off for local sites.** Host project switches
  off `web.config` rewrite rules that redirect to `https://` (the local site is
  HTTP-only, so it would never load), marks them with a *Disabled by DNN
  Manager* comment, and shows a ⚠ warning in the Activity log - a new warning
  style - to switch them back on before deploying to production.
- **Selectable activity log.** Text in the Activity log can be selected and
  copied, across lines too (mouse, Ctrl+A / Ctrl+C, right-click Copy). The
  **Copy** button copies the selection, or the whole log when nothing is selected.

### Changed

- **Projects table.** The columns are Name, Site, DNN (version from
  `bin\DotNetNuke.dll`), Database, **SQL**, **IIS**, Size and Path. SQL shows
  **Live** (green) when the project's database is on the SQL Server, **Offline**
  (red) when the server doesn't answer and *(none)* when the database doesn't
  exist; IIS shows **Live** for a started site, **Offline** otherwise and
  *(none)* without one. The toolbar is **Open site** and **Remove…** on the left
  and **Refresh** on the right; Open folder is in the right-click menu. The Host
  project folder list shows the IIS state the same way.
- **"Existing folder" is now "Host project".** Its options say *database*
  instead of *local database* - the database can be on any SQL Server, and the
  prompts name the actual server.
- **SQL Server connection check replaces the Docker check.** New project, Host
  project, Clone, the Projects page and the Environment page log in to the SQL
  Server from Settings (host, port and sa password, 5 s timeout) instead of
  querying Docker. The project flows no longer start the container; when it
  isn't reachable the database steps are skipped or fail with a clear message.
- **Clone always uses the source's `web.config`** for the source database (its
  `SiteSqlServer` connection).
- **`docker-compose.yml` is generated from the settings** (container name, sa
  password, edition, collation, port, volume) and no longer defines a fixed
  network / subnet, which clashed with other compose projects ("Pool overlaps
  with other one on this address space").
- **Settings.** The SQL Server card (was *SQL Server container*) holds the host -
  now **Server host**, `localhost` by default - port, sa password and the
  container values; the page uses the full window width.
- **DNN icon.** `dnnmgr.exe` (Explorer, taskbar, Alt+Tab) and every window's
  title bar show the DNN logo mark, drawn from DNN's own vector logo at all
  Windows icon sizes (16-256 px).
- **Questions and warnings use the app's own dialog** instead of the plain
  Windows message box: themed (light / dark), with an icon, selectable text and
  the default answer as the primary button (Enter picks it, Esc answers No).
- **Code comments cleaned up** - comments that only repeated the code, and
  stale ones about removed features, are gone.

### Performance

- **Faster start.** Publishing for a runtime (`-r win-x64`, as the publish task
  does) precompiles the app (ReadyToRun), so it starts without JIT-compiling
  everything first. The exe grows by about 9 MB.
- **Projects shows at once when you come back to it.** The last list is kept
  between visits and shown immediately while it refreshes in the background.
- **Host project opens without a pause** - IIS's configuration is read off the
  UI thread.
- **The first right-click on a project can't freeze the window** - the
  installed IDEs / SSMS are looked up once, shared with the background warm-up.

### Fixed

- **Removing a project deletes the folder even when something still uses it.**
  When files are in use, Remove finds the programs holding them - with the
  Windows Restart Manager (open files) and each process's working folder (a
  terminal or editor opened in the project) - lists them, and after a
  confirmation closes them (politely first, then forced, helpers included) and
  deletes the folder. Windows itself, services and Explorer are never closed;
  whatever is still locked is deleted at the next Windows restart. Before, it
  gave up with "A file is still locked" and still reported the removal as
  finished.
- **Remove reports every step.** The IIS and database steps say what they did,
  and a failed database drop is reported instead of passing silently.

### Removed

- **FTP.** Cloning over FTP and the FTP folder browser are gone (along with the
  FluentFTP package). Clone copies from a local folder.
- **Live sites page and saved connections.** The page and `connections.json`
  are no longer used.
- **Source database credentials on the Clone page** - the source database always
  comes from the source's `web.config`.

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
