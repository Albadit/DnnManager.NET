# Changelog

All notable changes to DnnManager.NET are documented here.

## Unreleased

### Changed

- **Closing the window no longer shows a Windows notification** that DNN
  Manager is still running - its icon by the clock stays. An operation that
  fails while the window is hidden still says so in one.
- **Shorter messages**: a toast says a long message in its first sentence; the
  whole of it is in the log file and behind **Details** (or **Show output**).
  Settings' and Troubleshoot's explanations are one line each.
- **Menus and drop-down lists look like the toasts**: the gear menu, right-click
  menus, the tray icon's menu and drop-down lists have the card's quiet border
  and a soft shadow instead of a light grey border. Text boxes and drop-downs
  have a quieter border too; the focused one still shows the blue one.
- **Sharper text in the tour, Settings, toasts and the command palette**: their
  shadow was an effect on the box itself, which drew all its text as a soft
  bitmap without ClearType; it is a layer behind them now, and the tour's card
  sits on whole pixels.
- **Settings → Keyboard shortcuts opens faster**: its 70-odd rows are kept
  instead of made again each time the category is shown.

### Fixed

- **Programs in Program Files and Windows start again with administrator
  rights**: since 1.8.1, DNN Manager had a wrong ID for Windows' TrustedInstaller
  account - which owns them - and refused every one of them as "programs without
  administrator rights could change it": `dotnet` (so SqlPackage, and with it the
  backup before an upgrade, Back up and Export for deployment), PowerShell,
  `schtasks`. A refused program's message now says why it was refused.
- **Docker Desktop installed for your account only works**: it was taken for
  *Not installed*, and every docker step failed. Its `docker` now runs as you,
  without administrator rights; backups go in and out of the container through
  `docker exec` instead of `docker cp`. Another program found only on your own
  PATH is named as such instead of "isn't installed".
- **The SQL container answers at `localhost` again**: since 1.8.1 it was
  published on `127.0.0.1` only, and Windows tries `localhost` as `::1` first -
  DNN Manager (and sites whose web.config says `localhost,1433`) waited until
  their time ran out. It is published on `[::1]` too now; **Set up
  docker-compose** once to get it.
- **"DNN couldn't connect to its database" says why**: DNN Manager tries the
  site's own connection string and names SQL Server's answer (or that it works
  from DNN Manager, so the site's own process can't reach it), and shows what
  DNN logged - its log4net files, or the Serilog ones of DNN 10.4.
- **New sites on the SQL container install again**: since the container is
  published on this PC's loopback only (1.8.1), a site's `localhost,1433` wasn't
  reached - .NET Framework's SqlClient, DNN's, goes to localhost by this
  computer's name and network address - and DNN's installation failed with
  *Could not connect to database*. Sites get `127.0.0.1,1433` now, DNN Manager's
  own connections use it too, and **Set up docker-compose** points existing
  sites that say `localhost,1433` at it.
- **Remove…** drops the site's login while a session of it is still open.
- **Set up docker-compose with a volume made by 1.7.1 or older** finds its old
  default `sa` password (`Admin@123`), takes it into Settings → Database server
  and makes the container again with it - instead of waiting three minutes and
  reporting a network error. It says when the data volume exists already.
- **Set up docker-compose** is off until the Docker card's test finds Docker's
  engine running, and says what is missing; the card tests by itself when it is
  first shown. **Install Docker Desktop** says when winget finds it already
  installed.
- **Restore backup** tries a file that is still in use again, as it was meant
  to: the retry never ran, so a DLL the worker process or `bin\roslyn`'s
  compiler still held failed the restore - and an upgrade's way back - at once,
  with the site's files half put back. A damaged file in the backup's zip fails
  the restore with a message instead of an unexpected error.
- **Export for deployment** of a site whose file is `Web.config` puts one
  web.config in the package - the prepared one. The local one, with this PC's
  connection string and debug on, stayed next to it.
- **Reset to defaults** gets past an `sa` password encrypted by another Windows
  account or on another PC: it was kept, and the next start stopped on it again.
  Enter the container's password again in **Settings → Database server**.
- **A new project named like one renamed since** gets a login of its own
  (`dnn_shop_2`): it gave the renamed site's `dnn_shop` a new password - locking
  that site out - and a failed or cancelled run dropped it. **Remove…** drops a
  renamed site's own login too, which it left behind.

## v1.8.2

### Upgrading

- **New installations go to Program Files.** Setup asks for administrator
  rights and installs for all users. An installation for your account only
  (the default up to 1.8.1) keeps updating where it is; a new Setup offers to
  move it - turn **Start DNN Manager when you sign in** on again afterwards.
- **Start at sign-in needs an installation for all users.** A sign-in task made
  for a per-user installation is removed at the next start, with a warning.
- **Terminals run as you, without administrator rights.** **New Administrator
  terminal** (the arrow beside **+**) opens one that has them - only with shells
  installed for all users, started without your profile scripts (`-NoProfile`,
  `cmd /d`, `bash --noprofile --norc`), and marked as such.
- **Settings that can be misused are refused.** The projects folder can't be in
  Windows, Program Files or ProgramData or in another user's folder; DNN release
  sources must be https; the hostname suffix must be a host name; an IIS
  feature's name a Windows feature name; Docker's container and volume names
  plain names. A saved value that breaks a rule goes back to its default at the
  start, with a warning naming it - the *can't use its settings* dialog is left
  for settings it can't read at all.
- **`DNNMANAGER_*` environment variables** are held to every rule the saved
  settings are, and stay applied after a Save; one that breaks a rule makes DNN
  Manager ignore them all, with a warning.
- **The SQL container is made again at the next Set up docker-compose**: its
  image is now pinned (`2022-CU27-ubuntu-22.04` by digest) instead of
  `2022-latest`. The databases stay in the volume.
- **Windows-authentication sites on another server** show no live SQL state
  unless that server is the one in **Settings → Database server**.
- **An Administrator terminal's Windows PowerShell** finds no modules in your
  Documents.
- **A SQL Server on another computer must have a certificate Windows trusts** -
  see [troubleshooting](.docs/troubleshooting.md).
- **Junctions made without administrator rights aren't followed** by DNN
  Manager (a module source linked into `DesktopModules`, say) - make them from
  an administrator prompt.

### Security

- **No more deleting in `%TEMP%`.** DNN Manager no longer cleans up the update
  folder 1.8.0 and older left in `%TEMP%` - with its Administrator rights, that
  delete followed wherever another program had pointed the folder.
- **Junctions aren't followed.** Windows' redirection trust is on for DNN
  Manager, and unpacking backups and DNN packages, **Clear website cache**,
  clones, undo and **Clean up data** check for links and junctions themselves.
- **Settings and environment variables can't widen DNN Manager's rights.** The
  projects folder guard changes only a folder of your own, never a system
  folder; `DNNMANAGER_*` environment variables are held to the settings' rules;
  .NET startup hooks are off; the command line isn't passed on to the elevated
  start.
- **Programs DNN Manager runs** are looked up on the computer's PATH, started by
  the path that was checked, and don't inherit startup hooks, profilers or your
  PATH; Explorer is started by its full path, SSMS and `docker compose`'s plugin
  only from admin-only folders, SqlPackage only from nuget.org.
- **Nothing of your environment makes them load other code.** Every .NET,
  NuGet, Docker and sqlcmd variable is dropped; Windows PowerShell finds only
  the modules of Windows and Program Files, not those in your Documents;
  docker reads a configuration of DNN Manager's own, not your `.docker` (its
  context, plugins and credential helpers); the computer's PATH is filled in
  from Windows' own folders. SSMS, an IDE started directly, the update helper,
  Setup and a restart start the same way. DNN Manager warns when it started
  with .NET profiler or diagnostics variables set.
- **An IIS feature's name can't run a command.** It must be a Windows feature
  name, and reaches PowerShell as data rather than as part of its script.
- **No Windows sign-in to a server a site chose.** With Windows authentication
  the monitor asks only a SQL Server on this PC or the one in Settings; a
  pipe's path (`\\server\pipe\…`) and a name merely starting with this PC's
  are no longer taken for this PC.
- **Sites can't change each other.** IIS_IUSRS and IUSR only read a site's
  folder and its app pool no longer changes its rights - sites made by an
  earlier DNN Manager, which gave all three Full control, are lowered at the
  start; a `C:\DNN` an administrator made loses everyone's groups.
- **What Windows deletes at the next restart** (a removed project's folder
  still in use) is first made Administrators' and SYSTEM's only, so nothing can
  turn it into a junction meanwhile.
- **Terminals run without administrator rights** (a restricted copy of DNN
  Manager's token: Administrators deny-only, no privileges, medium integrity);
  when one can't be made, none is started elevated in its place.
- **A launcher starts the installed DNN Manager**: `DnnManager-launcher.exe`
  (Native AOT, so no .NET runtime reads your environment) drops every `DOTNET_*`,
  `COMPlus_*`, `COR_*` and `CORECLR_*` variable, switches .NET's diagnostics
  off and starts `DnnManager.exe`. The sign-in task and Windows' administrator
  prompt start it; a sign-in task made by an earlier version is moved over to it
  at the start. The portable exe has none.
- **Setup** passes on only `Updated`, `Repaired` or `Uninstalled` as `/Done` and
  `/Back`, starts its own exe again only while it is the file that started, and
  ends a running DNN Manager by a checked path.
- **One way to start a program as Administrator**, which a test holds every
  other start to: SSMS, vswhere (now with a real time limit), Explorer and the
  IDE fallback go through it.
- **Unpacking** refuses names with `:` (alternate data streams), compares the
  files it must leave alone (`web.config`, the database) by their full path -
  `./web.config` and `WEB~1.CON` no longer get past it -, never writes into a
  hard-linked file, and refuses a package that wouldn't leave 512 MB free.
- **A site's XML files** are read without DTDs.
- **Kept DNN packages are checked offline too**: the SHA-256s GitHub lists are
  saved with the version list. A download redirected to http is refused as such.
- **A database other than the project's own** - named in its `web.config` -
  is dropped or changed only after a question naming both. **Open in SSMS**,
  portal-alias edits, Upgrade and Restore ask before signing in with your
  Windows account to a server that isn't on this PC or the one in Settings.
  **Close and delete** defaults to No.
- **Passwords:** a site login's password reaches `sqlcmd` on standard input,
  the container's health check uses `SQLCMDPASSWORD`, and a password put on the
  clipboard is taken off when DNN Manager quits. SqlPackage is pinned
  (`170.5.96`) and installed with NuGet's source mapping.
- **DNN Manager's temporary folder** keeps its owner from changing its rights,
  also where Group Policy makes the user the owner of what an administrator
  makes.
- **A server's backup folder** is used only when it is a local, full path.
- **The sign-in task** starts only at sign-in, not on demand.
- **DNN packages** are downloaded over https into the admin-only temporary
  folder and checked against GitHub's SHA-256 when it gives one - a kept package
  too, every time it is used.
- **Remote SQL Servers' certificates are checked**; the container's `sa`
  password goes to `sqlcmd` through an environment variable, not its command
  line.
- **The DNN install template** (with the host password) is readable by the
  site's own app pool only while DNN installs.
- **Setup** hands over to a newer Setup from its own admin-only folder, after
  checking it again; uninstall removes the sign-in task, asking for
  administrator rights when needed.
- **Releases are built on GitHub** by the release workflow, with a build
  provenance attestation (`gh attestation verify <file> --repo
  Albadit/DnnManager.NET`), and stay a draft until the tests and the build pass.
  A published release can't be redone. Signing is prepared, off until set up.
- **A release is published only once a person approves it**: the draft is made
  once CI, the build, the signing and a smoke test (Setup installed, DNN Manager
  started through its launcher, uninstalled; the portable exe started) have
  passed; the publish job waits for the `publish` environment's reviewer, checks
  that the tag still points at the commit built and the draft's files are the
  ones built, and makes it the latest only when it is newer. The job that builds
  gets no OpenID Connect token; the one that signs restores and tests nothing.

### Added

- **Keep backups and deployment packages** (**Settings → Projects → Backups**, `backups.keepDays`):
  backups and deployment packages older than that are deleted at the start.
  Off by default - a clone's copy of its source database is kept, and the clone
  says where.
- **Clean up data → Old settings files**: the files older versions kept their
  settings in (which may hold the `sa` password in plain text) and damaged
  settings databases.
- **Stop undoing**: Cancel, pressed again while an operation is being undone,
  stops the undo and names what wasn't undone. Quitting offers **Quit now** when
  an undo takes long; each undo step has a time limit.
- **An operation that didn't finish** (DNN Manager ended, Windows shut down) is
  named at the next start, with what it may have left half done.
- **Palette commands** for IIS Start, Stop and Restart, and a new Administrator
  terminal.
- **Start-up warnings show as a toast** too, not only on the Output tab: settings
  set back to their defaults, a Documents folder that OneDrive or a network
  share copies, Windows' Developer Mode, .NET profiler variables.
- **New Administrator terminal** (the arrow beside **+**): a terminal with DNN
  Manager's rights when you need one - shells installed for all users only (the
  others greyed, with why), no profile scripts, titled *Administrator: …* with a
  warning above it.
- **Uninstalling asks what else to remove**: it lists what stays (your
  `Documents\DnnManager`, the saved passwords, `%ProgramData%\DnnManager`,
  Docker's container and volume, your projects) and offers to remove your DNN
  Manager data - *No* by default. Docker, your projects and IIS are never
  touched.
- **A failed update keeps its logs** in
  `Documents\DnnManager\logs\update-failed-<version>.log` (the newest two), and
  **Show log** opens that file.

### Changed

- **Cancel stops the SQL Server work too.** A backup or restore cancelled in the
  container is ended there before the undo runs, and the undo's drop waits for
  it.
- **Host project restores beside the database** and swaps it in once complete,
  like Clone and Restore - a failed restore leaves the database as it was.
- **Restore checks the whole backup first** and writes nothing when an entry is
  unsafe; it says so when the site's files are left a mix.
- **A package with an unsafe path is refused** as a whole, not unpacked without
  that entry.
- **Accessibility:** icon buttons are reachable with Tab; the focus ring shows
  on every button; links have their own colour (contrast 4.8:1 or more, also on
  a selected row); field errors become the field's help text and are announced;
  toasts wait their turn instead of replacing each other; a project row tells a
  screen reader its DNN version, database and SQL state; the keep-warm buttons
  and the password eye say what they will do. Contrast of muted text, the
  current search match and the SUCCESS badge is 4.5:1 or more.
- **Long stages on the Output tab** show their first 200 and last 300 lines -
  every line is still in the log file.
- **Windows signing out or shutting down** while an operation runs is held up
  once: DNN Manager cancels and undoes for up to a minute, then quits - sign out
  or shut down again.
- **Programs DNN Manager starts end with it** (a Windows job object), and a
  cancelled backup on a remote SQL Server is deleted. Only a "file in use" error
  is tried again.
- **The log file** is written in batches (every 2 seconds; warnings, errors and
  how an operation ended at once), and all the logs together stay under 200 MB.
- **The update waits for Setup** however long it takes.
- **The terminal's search highlight** is darker (dark theme) and lighter (light
  theme), so the text under it stays readable; the Output tab's labels
  (**WARN**, **ERROR**, **SUCCESS**) grow with the terminal font size.
- **Releasing**: `redo-release.ps1` refuses while a release run on the tag hasn't
  ended, and amends only files git has - it asks about new ones. CI fails when a
  fast test is skipped, or more integration tests than `CI_MAX_SKIPPED_TESTS`;
  it builds with warnings as errors, XML comments included; `global.json` takes
  exactly SDK 10.0.401. Building the installer needs Visual Studio's C++ build
  tools for the launcher (`build.ps1 -NoLauncher` without them).

### Dependencies

- `Microsoft.Data.SqlClient` 6.0.2 → 6.1.7 (it brings MSAL's broker, with its
  native `msalruntime.dll`).
- `MSTest.TestAdapter` / `MSTest.TestFramework` 4.4.1 → 4.5.1.

### Fixed

- **After Uninstall from Setup's first page**, Setup comes back as a new install.
- **The hostname suffix** must be a real host name (no label starting or ending
  with `-`, not an IP address); a `DNNMANAGER_*` suffix is trimmed as the saved
  one is.
- **The `LIKE` pattern for host names** escapes `_` and `%`, so `dnn_dev.me`
  no longer matches `dnnxdev.me`.
- **A temporary DNN package** is deleted when New project fails or is cancelled.
- **A failure in DNN Manager's temporary folder** is tried again at the next use
  instead of failing until a restart; files left there for more than a day are
  deleted.
- **Export for deployment** no longer reads a `configSource` given as a full
  path - only the files inside the site go into the package's changes.

## v1.8.1

### Upgrading

- **Old installations keep their passwords.** A new installation makes up its own
  `sa` password and has no default DNN host password; an existing one keeps what
  it has - if that is still `Admin@123`, change it in **Settings → Database
  server** and **Settings → Projects**.
- **The projects folder is made private.** At its first start DNN Manager sets
  the projects folder (`C:\DNN`) to SYSTEM, Administrators and you - every other
  account on the PC loses access to it. Windows passes the change on to every
  file in it, which can take a moment with many sites.
- **Programs run with Administrator rights only from Program Files.** `docker`,
  `dotnet`, `winget`, PowerShell 7 and Git Bash are used only when installed for
  all users. SqlPackage is installed again, into `%ProgramData%\DnnManager\tools`,
  the first time it is needed - a copy in `~\.dotnet\tools` isn't used.
- **Sites made before keep signing in as `sa`**; new ones get a login of their own.

### Fixed

- **Import never touches the project it was exported from.** Importing a zip made
  by Export on this PC under a new name restored into - or shared - the original
  project's database, and moved its portal alias. The new project now always gets
  a database of its own, and its `web.config` is pointed at it.
- **No project replaces another folder's IIS site.** New project, Clone, Import
  and Host project removed an IIS site of the same name serving another folder.
  Such a name is refused - as you type, and before anything is made.
- **A failed operation leaves nothing half made.** New project, Clone, Import and
  Host project left their folder, site and database behind when they failed (an
  offline download, an IIS error), so the same name couldn't be used again. What
  they made is now taken away again, as after **Cancel** - only a failed DNN
  installation is kept to look into.
- **Quitting during an operation waits for it** to stop and put back what it did,
  instead of cutting it off half way.
- **An upgrade step that throws is rolled back** - before, an unexpected error
  left the site half upgraded.
- **Clone checks everything before copying a file**: the source's database (read
  from the source's own `web.config`), the local SQL Server (not reachable: the
  clone stops, rather than leaving a copy that uses the source's - possibly live -
  database), and a database of the clone's name, which it now asks before
  replacing. The copy goes in under a name of its own and replaces the old one only
  once it is complete.
- Clone from a SQL Server with Windows authentication, and from SQL Server
  Express (no backup compression), works; a local server's backup goes to its own
  backup folder.
- **Remove** keeps a database another IIS site uses too, says when a database
  couldn't be dropped, and offers to delete the project's backups.
- **Restore backup** imports the database before it touches the site's files: a
  failed import leaves the project as it was.
- Portal aliases keep the site's port, and DNN's `objectQualifier` is respected
  when clone, import and host fix the aliases and SSL.
- A failed drop or restore no longer leaves the database in single-user mode;
  rename and drop work on Azure SQL Database.
- Editing bindings keeps each binding's IP address; stopping a site doesn't stop
  an app pool another site's application uses; restart right after stop starts the
  site; renaming a site only in capitals works; creating a site joins an app pool
  of its name that other sites share.
- The hosts file: its block is found after a byte order mark, international names
  are written in punycode, a copy is kept as `hosts.dnnmanager.bak`, and it is
  never written before IIS has been read.
- The upgrade's local install never overwrites `web.config`, and its binding
  redirects keep a `codeBase` and are set in whichever `assemblyBinding` they are.
- DNN's release list reads every page, so the oldest versions are offered too.
- Export for deployment finds `Web.config` with any capitals; a zip export doesn't
  fail on a file older than 1980.
- Sites sharing an app pool show their CPU; a SQL Server address without a port is
  the container only when it publishes 1433.

### Changed

- **Security:** updates are staged in `%ProgramData%\DnnManager\temp` (only
  administrators can change it) and checked again by the helper right before it
  runs them; a release without GitHub's SHA-256 isn't installed. Editors open as
  you, not as Administrator. A site's own app pool alone may change its folder.
  `sqlcmd` runs with `-x -X`. See [security.md](.docs/security.md).
- **Accessibility:** every field and icon button has a name for screen readers;
  the Logs tab and the terminal are read as documents; toasts are announced;
  light-theme error, success and warning text, field borders and the dark theme's
  focus ring meet WCAG contrast; Windows Contrast themes are followed. **Ctrl+A**
  ticks every row; the command palette can choose columns, sort the projects and
  resize the sidebar and panel. **Create project** says what is still needed.
- **Settings → General → Look for a newer DNN Manager when it starts** - off, only
  About asks GitHub.
- The Output tab no longer lists the sites kept warm under *Background* - the
  Projects page shows them already.
- **Troubleshoot → Clean up data → Deployment packages**; Export for deployment
  says what its package holds.
- A password put on the clipboard for SSMS stays out of Windows' clipboard
  history and cloud clipboard, and is cleared after 60 seconds.
- Faster: the Output tab updates once a second, project files are copied four at
  a time, search has a time limit, a LocalDB database isn't asked every 10 seconds
  (which kept LocalDB running), catching up a busy log after a pause reads only its
  end, zips are made with fast compression.
- Downloads that stop getting anything fail after a minute; Docker's checks after
  30 seconds. The settings database's backup is renewed once a day; a day's log
  file stops at 50 MB and old ones go when the day changes.
- **Releases:** published only once CI has passed on the tag, with every file
  checked against GitHub's SHA-256 and a `SHA256SUMS.txt`; CI runs on `main` and
  pull requests too, with pinned actions, `global.json`, NuGet lock files and
  Dependabot; the installer is compiled with a hash-checked Inno Setup. See
  [releasing.md](.docs/releasing.md), which also has a runbook for a bad release.

## v1.8.0

### Added

- **Sites open without internet.** `*.dnndev.me` is found through public DNS,
  so with the PC offline the browser couldn't find a local site, though IIS and
  DNN were running. DNN Manager now keeps every site's host name in Windows'
  hosts file (`127.0.0.1 mysite.dnndev.me`), in a block of its own between two
  marker lines - the rest of the file stays as it is. It is written when DNN
  Manager starts and whenever a site is added, removed or gets other host
  names (also one made in IIS Manager), and it stays when DNN Manager is
  closed, so the sites open offline after a restart of the PC too.
- **Custom domains.** A site bound to a host name of your own - `shop.test`,
  `klant.local`, `www.customer.nl` - gets its line too, so it opens on this PC
  without editing the hosts file. New project's hint says so; a real domain
  then opens the local site on this PC instead of the live one.
- A hosts file that can't be written (read-only, blocked by security software)
  shows a warning instead of the sites silently needing internet.
- **New project checks the host name and the database as you type.** A host
  name and port that are already another IIS site's address are refused at
  once - IIS would take the second binding and then not start the new site,
  and DNN's installer would talk to the other one. The database server is
  asked whether the database is free (*Checking…*, *is free*, *already
  exists*, or why it can't be asked - asked again every 10 seconds).
  **Create project** waits until both pass, instead of the setup stopping
  after the download. With *Manual DNN setup* an existing database is still
  allowed. The setup checks the host name against IIS again before it
  downloads anything.
- **Settings → General → Play animations**: off, the panel and the sidebar
  stop sliding, toasts and menus stop fading, switches stop sliding, and the
  state dots, flames, spinners and progress bars stop moving - everything
  changes at once. They are also off while Windows' animation effects are off
  (Settings → Accessibility → Visual effects).
- In the **Output** tab, click a stage (or **Tab** to it and press **Enter**)
  and the log scrolls to it.
- The **Logs** tab shows the hosts file: **DNN Manager** → **Hosts file**, first
  in the list under a section of its own (*Windows*), the whole file, shown
  again from the top whenever it changes.

### Changed

- **Faster with many sites.** The search filters as you type without a pause -
  about 7 ms a key instead of 230 ms with 18 sites (600 ms with 50): it looks
  again only at the rows it shows or hides, instead of making every row again.
  The Projects table makes only the rows on screen (it still scrolls by pixels),
  so 50 sites show in half the time and sort in half the time. After an
  operation on a site only its folder is measured again for the *Size* column,
  not every site's (seconds of disk with many sites). IIS's worker processes are
  read in one call instead of one per app pool.
- **No more stalls from UI Automation.** When a program on the PC listens to
  UI Automation - Windows' text input, PowerToys, screen readers do - WPF told
  it about every cell, text and resize grip of the Projects table that changed:
  the CPU and memory figures every two seconds, every row when the table was
  sorted or shown again - up to half a second of the window not answering. The
  table now has one element per row, named after its project ("shop, Running,
  http://shop.dnndev.me"), with only its buttons, check box and links in it: a
  sort takes about half the time, following the sites less than half, and a
  screen reader reads the project instead of "ProjectRow".
- A site's overview opens faster: only the tab shown is built (the others when
  you choose them, not three times each as the overview reads the site), and
  its database is read off the UI thread.
- A SQL Server that doesn't answer (the container stopped) is asked once every
  10 seconds instead of once per site - each was a connection waiting 5 seconds
  on a thread of its own.
- The worker processes' figures are read from a handle kept open per process,
  and new ones are found from the list of process IDs - not a snapshot of every
  process on the PC every two seconds.
- Toasts (the running operation, messages) sit at the bottom-right of the page,
  above the bottom panel - no longer over the Output or a terminal.
- The bottom panel slides open whatever opens it - a toast's **Show output**,
  the running operation's toast, **View logs**, a new terminal, a command - as
  it does with its button; it opened at once before.

## v1.7.9

### Fixed

- The site overview's **Assemblies** check reported assemblies as *Missing*,
  a *Version conflict* or a *Binding redirect* to a version `bin` doesn't have
  when `web.config` loads them from a folder of their own with `codeBase`
  (`bin\Imageflow`, `bin\2sxc`). It now reads every `assemblyBinding`, every
  `bindingRedirect` of an assembly (one per version range) and the `codeBase`
  entries, and checks the files they point at.

### Changed

- **Assemblies** reads every DLL under `bin`, its folders too: the count per
  folder, and under **Not loaded by ASP.NET** the ones neither the probing path
  nor a `codeBase` points at. An assembly in such a folder finds what is next
  to it; the compiler's in `bin\roslyn` (it has `csc.exe`) aren't checked
  against the site.
- A reference to an assembly that is only in such a folder says where it is -
  *bin\Imageflow has 2.2.0.0, but web.config has no codeBase for it*. A
  `codeBase` whose file isn't there is reported as **Code base** (red).
- Messages name the folder of each version (*the site has 8.0.0.0 in bin,
  9.0.0.0 in bin\2sxc*), and **Configuration** shows how many **Code bases**
  `web.config` has.
- Assembly problems are listed only in **Assemblies**, no longer again in
  **Detected issues**; **Project health** on *General* counts them in a row of
  their own, **Assemblies**.

## v1.7.8

### Upgrading

- Releases no longer carry their files under the old names
  (`DnnManagerSetup-…`, `DnnManager-…`) too - only `DnnManager_Setup-…` and
  `DnnManager_Portable-…`. DNN Manager 1.7.6 and older can't install this
  version with **Update**: install 1.7.7 or newer by hand once (or update to
  1.7.7 first while it is the latest). 1.7.7 and newer update as usual.

### Fixed

- A new project with a **LocalDB file** failed at **Creating host account** -
  *"cannot be opened because it is version 998. This server supports version
  904 and earlier"* - on a PC with two LocalDB versions (Visual Studio's 2019
  next to 2025). DNN Manager opened the site's file in your own `MSSQLLocalDB`,
  which can be older than the site's; it now opens it in a LocalDB of the
  file's own version, making an instance of its own (`DnnManager17`) when yours
  is another version.
- Missing IIS features you agree to enable no longer leave **Checking IIS**
  marked as failed: they are listed as missing, and only a feature that still
  can't be enabled - or that you chose not to - is an error. *A restart may be
  needed* is said only when Windows says so.
- DNN Manager's database (`dnnmanager.db`) could be left unusable when two
  threads opened a new file at the same moment: both made its tables, the
  count of table versions went back, and every later start failed. Its table
  changes now run in one transaction, counted under the write lock.
- A damaged `dnnmanager.db` stopped DNN Manager at its start - even **Reset to
  defaults** failed on it. It is now put aside (`dnnmanager.damaged-<date>.db`)
  and DNN Manager goes on from its last copy or the defaults.

### Changed

- Before an update changes the tables of `dnnmanager.db`, a copy of it is kept
  as `dnnmanager.backup.db`.
- An older DNN Manager started after a newer one leaves a `dnnmanager.db` with
  newer tables alone and says so, instead of writing into it.
- LocalDB: DNN Manager's own instance (`DnnManager17`) is checked by connecting
  to it, and a site's database file gets the folder's permissions before it is
  opened, not only after.
- The native SQLite comes with `Microsoft.Data.Sqlite` (3.53.3); the separate
  pin of `SQLitePCLRaw.lib.e_sqlite3` is gone, and a test checks it has the fix
  for CVE-2025-6965.

## v1.7.7

### Upgrading

- The release files are renamed: `DnnManager_Setup-<version>-x64.exe` (was
  `DnnManagerSetup-…`) and `DnnManager_Portable-<version>-x64.exe` (was
  `DnnManager-…`). Each release also carries the same files under the old names,
  so **Update** in DNN Manager 1.7.6 and older still installs it.

### Added

- **Getting started guide** on the very first start: eight steps, written for
  someone who has never used DNN Manager, IIS or DNN - what it does, what to
  set up first, the parts of the window, what the Projects table shows, what
  every button does (each marked *Safe*, *Careful* or *Can't be undone*), logs
  and Troubleshoot, updates. Skip it at any time; it isn't shown again (nor to
  anyone who used DNN Manager before). A later version that adds a page shows
  only that page. A new user isn't shown release notes: **What's new** stays
  out of Help, Settings → Help and the command palette until their first
  update.
- **Tour of the window** - from the guide's last step or Help: one part at a
  time, ringed, with a card saying what it is for. Back, Next, Skip tour,
  Finish.
- **Help** - a **?** in the title bar: help for the page shown (**F1**), the
  guide, the tour, keyboard shortcuts, What's new, the user guide. Also
  **Settings → Help** and *Help: …* in the command palette.

### Changed

- The tooltips of the buttons that do something say what they do and what it
  affects - *Stop - turn the website off until you start it again; nothing is
  deleted* instead of *Stop*: the Projects table, its bulk actions, a project's
  details, IIS in the status bar, the panel's tabs, Troubleshoot, the sidebar.
- **F1** opens the help for the page shown. A shortcut you had set to F1 is
  flagged in **Settings → Keyboard shortcuts** as used twice.
- The documentation moved from `docs/` to `.docs/`. The release notes built
  into DNN Manager, the release scripts, the VS Code tasks and the links
  (README, **Settings → About → Documentation**) follow.
- The workflow on GitHub is now `.github/workflows/ci.yml` ("CI", was
  `release.yml`): it only builds in Release and runs the tests when a version
  tag is pushed; it publishes nothing. Releases are published from VS Code with
  **release (GitHub)** (or **release: redo (GitHub)**), whose tag it then
  builds and tests.

## v1.7.6

### Upgrading

- Closing the window now keeps DNN Manager running, with its icon in the
  notification area - also for an installed version updated to this one. Quit
  from that icon, or turn **Keep DNN Manager running when you close the window**
  off in Settings → General to have closing quit again.
- **Settings → General → Save resources while the window can't be seen** is
  gone: it is always on. A `window.saveResourcesWhileMinimized` row saved by an
  older version stays in the settings and is ignored.
- **Settings → General → Enable the terminal** is gone: the terminal is always
  there. A terminal switched off before is back; a `terminal.enabled` row saved
  by an older version stays in the settings and is ignored.

### Added

- **Keep DNN Manager running when you close the window** (Settings → General,
  on by default): closing the window hides it and DNN Manager keeps running -
  operations, terminals, following the sites and keep warm go on. Its icon in
  the notification area opens the window again (a click) or quits (right-click
  → **Quit DNN Manager**, also in the command palette); starting DNN Manager
  again shows the window too. While hidden, the window saves resources as when
  minimized, and a failed operation shows a Windows notification.
- **Settings: General**, **Projects**, **DNN releases**, **Database server**,
  **Docker container**, **IIS**, **Keyboard shortcuts** and **About** in the
  command palette open Settings on that category (they can have a shortcut too,
  in Settings → Keyboard shortcuts). *Open Keyboard Shortcuts* is now
  **Settings: Keyboard shortcuts**.

### Changed

- **Setup** opens while DNN Manager is running - no "Setup has detected that DNN
  Manager is currently running" first. When DNN Manager is installed, its first
  page has a button for **Update to X**, **Repair** and **Uninstall** instead
  of a choice and Next; once one is done, Setup comes back to that page (*Repair
  finished.*, *Update finished.*), or to a new install's after an uninstall.
  Just before Repair or Update replace the files, and once an uninstall is
  confirmed, a running DNN Manager is closed without asking (a running operation
  is cancelled); one that doesn't close - 1.7.5 and older can't be asked - is
  ended, after Windows asks for administrator rights. After Repair or Update it
  is started again.
- Saving resources while the window can't be seen is always on - it only pauses
  what nobody can see, and everything is brought up to date as soon as the
  window is seen again.
- The terminal is always on: the Terminal tab, **New terminal** and **Open in
  terminal** are always there; Settings → General keeps its shell and font.
- The repository moved to [Albadit/DnnManager.NET](https://github.com/Albadit/DnnManager.NET):
  update checks, the installer's links and the README point there, and the
  publisher (Windows' installed apps) and the licence's copyright holder are
  now Albadit. Installed versions keep updating - GitHub redirects the old
  address.

## v1.7.5

### Added

- **Upgrade DNN…** (the overview's **DNN** tab, the right-click menu, the
  Details **⋮**, the command palette) upgrades a project's DNN along DNN's
  suggested upgrade path - every listed version on the way, one step at a time
  (see `docs/dnn-upgrades.md`):
  - **an analyser and a plan before anything changes**: .NET Framework, SQL
    Server, the app pool, files and database versions, extensions (built for
    which DNN, using Telerik), custom settings, content, free space - each
    Compatible, Warning, Blocking or Unknown; nothing starts while anything
    blocks;
  - **per step**: a backup (files, database, web.config, the site's inventory
    and the plan), the step's files (the upgrade package; from DNN 10.2 on the
    install package as DNN's own local upgrade puts it in, binding redirects
    included), DNN's unattended upgrade (`Install.aspx?mode=upgrade`, its whole
    answer kept), a restart (waiting for the site's worker process to end and
    deleting the lock DNN leaves), and checks - version, no content lost, every
    portal's pages, DNN's and Windows' logs;
  - **a step that fails, or Cancel**: the chain stops, the diagnostics are kept
    with the step's backup, the likely cause and fixes are said, and the site
    is put back to the last version that worked.
- **Restore backup** ▸ (right-click menu, Details **⋮**, command palette): puts
  a backup with the site and database back over the project - files added since
  are deleted; the backup's database is imported under a name of its own and
  only then swapped in for the project's, so a failed import leaves it
  untouched. The site's worker process has ended before anything is replaced.
- **Recently used commands** in the command palette: the last 8 commands run
  from it come first, marked *recently used*, then the *other commands* - also
  while searching - and are kept for the next start.

### Changed

- A project command chosen in the command palette (**Rename project…**,
  **Edit host names…**…) always asks which project - the selected one first -
  instead of acting on the selected one straight away. Shortcuts (**F5**…)
  still act on the selected project.
- The command palette no longer lists **Show all commands** - it is already open;
  **Ctrl+Shift+P** still opens it.

## v1.7.4

### Upgrading

- DNN Manager's log files of the old name (`logs\dnnmanager-2026-10-04.log`)
  are deleted at the first start; new ones are `dnnmanager-20261004.log`.

### Added

- **What's new after an update**: the first start of a newer version shows the
  release notes of every version since the one before - the same text as the
  GitHub releases, built into DNN Manager so they show offline too - whether it
  was updated by **Update**, Setup or a new portable exe. **What's new in this
  version** in the command palette shows them again.
- **DNN Manager's own log on the Logs tab**: **DNN Manager** is first in its
  list of sites, with its daily log files (the newest 8) - followed as they are
  written, `[warning]` lines yellow and `[error]` red. **Show DNN Manager's
  log** in the command palette opens it.

### Changed

- DNN Manager looks for a newer release once, a few seconds after it starts, not
  every hour - and still when **Settings → About** is opened and on **Check for
  updates**.
- DNN Manager's log file is named by its day without dashes,
  `logs\dnnmanager-20261004.log`.

## v1.7.3

### Upgrading

- **The settings start from their defaults.** DNN Manager's data is now one
  SQLite database, `Documents\DnnManager\dnnmanager.db`, and what earlier
  versions saved isn't read: set your settings again in the app (Settings,
  Customize Layout, Keyboard shortcuts, the Projects table's columns), and
  switch keep warm on again for your sites. The old `settings.json`, `state\`
  and `projects\` stay where they are - delete them once you no longer need
  them. Your projects, backups, logs and kept packages aren't touched.
- Keep warm is the same for every site: an interval or pages of a site's own
  (only set by hand in `state\keep-warm.json`) are gone - every site follows
  **Settings → Projects → Keep warm**.

### Added

- **Host project shows whether each folder's database is live** next to its
  IIS site: `DB: Live` (green), `DB: Offline` (red), `DB: not created`, `DB:
  LocalDB file` or `no database` - asked with the folder's `web.config`
  connection, as the Projects table's SQL column asks, after the list is shown.
  The IIS and database texts line up in every row.
- **Settings → IIS → Edit…** adds or removes the IIS Windows features DNN
  Manager checks and enables - a row each, with **Add feature** and **Use the
  defaults**, saved at once (was only in `settings.json`,
  `iis.requiredFeatures`).

### Changed

- **One database instead of JSON files.** The settings, the workspace (window,
  page, forms, Logs tab), how each project was installed, which sites are kept
  warm and the saved DNN versions are in `Documents\DnnManager\dnnmanager.db`
  (SQLite) - one row per value, each write whole or not at all, nothing to edit
  by hand. `Documents\DnnManager` keeps `backups`, `deployments`, `logs` and
  `packages` besides it.
- **New project's DNN versions are asked of GitHub at every start** and saved
  in `dnnmanager.db`, whether or not packages are kept, so they are offered
  without internet; the **Refresh** button next to **Version** is gone (a
  failed lookup is tried again when New project is next shown).
- A reset to the defaults keeps no copy of the settings, and **Clean up data**
  has no *settings copies* any more.
- When the settings can't be used at start, the choices are **Try again**,
  **Reset to defaults** and **Exit** (no file to open any more).

## v1.7.2

### Upgrading

- Your settings keep their format.
- **Which sites are kept warm is now in `state\keep-warm.json`.** The per-site
  files of earlier versions (`projects\keep-warm\`) aren't read: switch keep
  warm on again for the sites you want it for (and set their own interval or
  pages again).
- Setup installs the newest release from GitHub from this version's Setup on;
  the 1.7.1 Setup still installs the version it carries.

### Added

- **Edit a project after it was set up**, from the Edit buttons on its Details
  tabs, the right-click menu and the command palette - each a small dialog, then
  an operation on the Output tab that **Cancel** takes back:
  - **Rename…** (General, right-click menu): the IIS site and its app pool, the
    folder when it is the project's in the projects folder, and - ticked by
    default - its host name `<old>.<suffix>` → `<new>.<suffix>` with DNN's
    portal alias, and its database when it is named like the project
    (`web.config` follows). The site is stopped meanwhile and started again;
    the new app pool identity gets the folder and, with Windows
    authentication, the database. A step that fails takes back the ones before.
  - **Edit host names…** (IIS): the site's http bindings - host name and port,
    added or removed; https bindings stay. DNN's portal aliases follow.
  - **Edit app pool…** (IIS): .NET CLR version, pipeline mode, 32-bit,
    identity, idle time-out and start mode of the site's own app pool.
  - **Change connection…** (Database): `web.config`'s `SiteSqlServer` pointed
    at the local SQL container, a SQL Server (Windows or SQL Server
    authentication) or a LocalDB file, with **Test connection**.
- **Export for deployment…** (Export ▸ on the right-click menu and the Details
  ⋮, and the command palette): a package for the live server in
  `Documents\DnnManager\deployments\<project>_<date>\` - `<project>.zip`
  without `.git`, `.vs`, `.vscode`, `.idea`, `node_modules`, DNN's logs and
  cache and the search index, with `web.config` ready for the server (the live
  connection string or a placeholder, debug off, the HTTPS rules DNN Manager
  switched off back on); `<project>.bacpac` with the live domains as portal
  aliases and DNN's SSL setting to match (changed in a temporary copy of the
  database); and `DEPLOY.txt`.
- **Setup installs the newest release from GitHub.** A new install and
  **Update to version X** get GitHub's newest, **Repair** downloads the
  installed version again; any version but its own is downloaded (GitHub's
  SHA-256 and its version checked) and handed over to. Without internet, Setup
  installs the version it carries, and offers it when a download fails.
- **New project works without internet** for kept packages: with **Keep
  downloaded DNN install packages** on, each repository's versions are saved
  (`packages\<owner>.<repo>\releases.json`) and offered when GitHub can't be
  reached.

### Changed

- **The status bar's figures keep their place.** RAM, CPU and Disk are each as
  wide as their largest value (all of the PC's memory, 100%, the drive full),
  and they show `RAM 0.00 GB`, `CPU 0.00%` and `Disk: --.-- GB used (limit
  --.-- GB)` until all three are measured, then change together.
- The Details ⋮ has the whole **Export** ▸ menu, as the right-click menu.
- Every folder in `Documents\DnnManager` is made at start; **Settings → About**
  lists **Deployments** too, and **Open** makes a folder that isn't there.
- A GitHub version lookup that doesn't answer within 30 seconds counts as
  offline (was 100), and a failed DNN download says what to do without internet.
- The bottom panel's header has as much room at its ends as above and below.
- Builds take their version from the newest `v*` tag - `DnnManager.csproj` has
  no `<Version>` to raise any more (see `docs/releasing.md`).

### Fixed

- Exporting the database of a site that signs in with Windows authentication
  exported from the local SQL container instead of the site's own database.

### Upgrading

- Your settings keep their format; `layout.menuBarVisible` is added with its
  default (`true`).
- The title bar (30 pixels) and the status bar (24) are lower in the Default
  density too.
- Under a narrow or hidden sidebar the IIS cell no longer has a **⋮** menu -
  its buttons are always there.

### Changed

- **The Default layout density draws the sidebar, the page and the panel as
  cards**, as VS Code does: rounded, with a gap between them, and the title bar
  and status bar one surface without lines between them. **Compact** keeps them
  flush, divided by lines. The title bar (30 pixels) and the status bar (24) are
  as low in both densities, so the cards get that room.
- **Drag the sidebar's edge** to make it wider or narrower (kept between
  starts). The edges between the sidebar, the page and the panel show three dots
  where they can be dragged (Compact: just a 1 px line, the panel's replacing
  the thicker bar) and turn blue while dragged; where the sidebar's and
  the panel's meet, both resize at once. The sidebar and the panel follow the
  pointer down to nothing and out again from their edge; let go below their
  smallest size (160 pixels for the sidebar, 90 for the panel) they slide out to
  it, below half of it they slide closed. In the Default
  density the cards reach down to the status bar.
- **Customize Layout → Menu Bar** shows or hides the app's name in the title
  bar (also *Show or hide the menu bar* in the command palette).
- **The IIS cell keeps its state and buttons** under the icons-only or hidden
  sidebar - as wide as what it shows, instead of a dot with a **⋮** menu.
- **The sidebar and the panel slide open and closed** from the title bar's
  buttons, **Ctrl+B** / **Ctrl+J**, the panel's **✕** and Customize Layout.
- **Softer layout icons.** The layout buttons in the title bar and the icons
  in **Customize Layout** have slightly rounded corners and line ends.
  **Default** and **Compact** density show the sidebar, editor and panel -
  spaced apart or packed into one window - and the **Customize Layout** button
  shows the Default icon. The **Columns** button above the Projects table has a
  rounded icon too.
- **The sidebar's entries and Settings' categories** have less room on the left
  and right and more between the icon and the name; in the sidebar the first
  entry and **Settings** are as far from its top and bottom as from its sides.
- **The status bar's figures** - RAM, CPU and Disk - are evenly spaced, and the
  IIS buttons are smaller, with a gap before them.
- **DNN Manager looks for a newer release every hour** (1.7.0 checked every six
  hours).

### Fixed

- The text in the status bar and the title bar sat below the middle, beside
  icons that are centred.

## v1.7.0

### Upgrading

- **The versions were renumbered.** The releases 2.0.0-2.5.0 are now
  1.1.0-1.6.0 - same contents, new numbers: 2.0.0 → 1.1.0, 2.1.0 → 1.2.0,
  2.2.0 → 1.3.0, 2.3.0 → 1.4.0, 2.4.0 → 1.5.0, 2.5.0 → 1.6.0. A DNN Manager that
  still says 2.x is one of these; Settings → About in a 2.x build counts itself
  newer than any 1.x release, so install the newest release over it by hand
  once.

### Added

- **DNN Manager updates itself.** When GitHub has a newer release, a blue
  **Update** button appears in the title bar, left of the layout buttons. It downloads
  the release's Setup (installed) or portable exe, checks its size, SHA-256 and
  version, remembers where you are, closes, installs it - Setup silently with its
  progress window, the portable exe replaced in place with a backup - and opens
  the new version back on the same page, project and Details tab. Anything that
  fails before closing leaves the current version running; a failed install
  starts the old version again and says why. Checked a few seconds after start,
  every hour, and on Settings → About.
- **DNN Manager opens where you left it** - after a close, a restart, an update
  or a crash: the window's place and size, the page, the Projects table (search,
  *Only show running*, sorting, expanded and selected rows, scroll position), the
  project whose Details were open and their tab, the Settings category, what was
  typed on New project and Host project, unsaved Settings changes, and the bottom
  panel (its tab, search, and the site and log on the Logs tab). Terminals and
  passwords are never kept - a shell doesn't outlive DNN Manager. Saved a
  moment after anything changes, in `Documents\DnnManager\state` (one file per
  area, written whole; one that can't be read is set aside and the defaults are
  used). Reset to factory defaults forgets it.

- **DNN Manager works from the keyboard**, like VS Code: a **command palette**
  (**Ctrl+Shift+P** - every command that makes sense now, with its shortcut;
  **Ctrl+P** - go to a project), shortcuts for the common actions (**F5** /
  **Shift+F5** / **Ctrl+Shift+R** start, stop and restart the selected project,
  **Ctrl+,** Settings, **Ctrl+F** search, **Ctrl+J** the panel, **Ctrl+`** the
  terminal, **Ctrl+1-3** the pages, **Ctrl+Tab** and **Ctrl+PageUp** the next
  page and tab, **Ctrl+W** close…), a blue focus ring wherever the keyboard is,
  and **Settings → Keyboard shortcuts** to search, change, reset and spot
  conflicting shortcuts - kept in `settings.json` (`keyboard.shortcuts`). Tab
  goes into the Projects table and out again; the bulk buttons are reached with
  Tab too.
- **Keep warm for the checked projects**: the bulk actions above the Projects
  table end with the keep-warm flame - it keeps every checked site warm, or, when
  they all are already, stops keeping them warm.
- **A search in the middle of the title bar**, as VS Code's: click it to open
  the command palette on the projects (type **>** for the commands).
- **Customize Layout**, as in VS Code - from the title bar's new layout buttons,
  the gear's menu or the command palette: show or hide the sidebar
  (**Ctrl+B**), the panel and the status bar; the sidebar on the left or right;
  the panel under the page only, the window's whole width, or to its left or
  right edge; the command palette at the top or in the center; a default or
  compact density (a narrower sidebar, a lower title bar and status bar). Applied and saved at once (`layout` in `settings.json`).
- **The theme is chosen from the gear's Themes** (or the palette's *Color theme…*),
  applied and saved at once - no longer on Settings → General.
- **The gear's menu**, VS Code's *Manage*: Command Palette, Settings, Keyboard
  Shortcuts, Themes (Dark, Light, System - applied and saved
  at once), Customize Layout, Troubleshoot and Check for Updates. A **Color
  theme…** command does the same from the palette.

### Changed

- **The running operation is a toast** over the bottom-right corner - its name
  (a click opens Output), **Cancel** and a moving bar, as VS Code shows a task in
  progress - instead of a part of the status bar.
- **The log file is one line per message** - its time, one space, the message
  (`18:21:16 Database seeded.`), without the operation and stage headings, which
  are the Output tab's.
- **The Dark and Light themes are VS Code's Dark Modern and Light Modern** -
  their own colours for the window, panels, lists, inputs, buttons, banners,
  the Output tab and search matches.
- **Search fields are lower** - 26 pixels, as VS Code's, and all as tall as the
  panel's search (the Projects search was stretched to the buttons beside it) -
  and plain, without the magnifier and the ✕. The command palette no longer has
  a line of help under its list.
- **The command palette opens at the top**, over the title bar's search, as in
  VS Code, with a compact list (22-pixel rows).
- **The sidebar's pages are rounded**, without the accent bar on the left; so are
  the panel's tabs (Output, Logs, Terminal) - the shown one on a background
  instead of underlined.
- **The window shrinks as far as VS Code's** - down to 400 × 270 pixels (it was
  900 × 600). In a short window the open panel gets lower and the status bar
  always keeps its place; a taller window gives the panel its height back.
- **The status bar's panel button is gone** - the title bar's panel button and
  **Ctrl+J** show and hide the panel.
- **The command palette says "No matching results"** as a row of its list, as VS
  Code does; choosing it does nothing.
- **The narrow sidebar is narrower** - 48 pixels, as VS Code's activity bar (it
  was 56) - and its icons are centred in their highlight.
- **Every button has a tooltip** - its name, as in VS Code - with its keyboard
  shortcut when it has one -
  the shortcut as set now, so one changed in Settings → Keyboard shortcuts shows
  there at once.
- **The gear moved to the bottom of the sidebar**, and Troubleshoot into its
  menu; the title bar has the layout buttons in their place. The sidebar no
  longer shows the projects folder - it is in Settings → Projects.
- **New project, Host project and a project's Details are centred** in the
  window, as Troubleshoot is.
- **The Projects table takes the keyboard on a click** - on a row's name,
  address or buttons too - so the arrows go on from there, as in the terminal
  list; **Del** removes the selected project (asking first).
- **The Projects table is rounded**, like the cards; a click in its empty space
  selects the first row, and leaving it clears the selection. **→** shows a row's
  details in the table, **←** hides them.
- **Settings and Troubleshoot open over the page**, like VS Code's modal
  editors - the page dimmed behind them, the title bar and status bar still
  working. **✕**, **Esc**, **Ctrl+W** or a click on the dimmed page closes them.
- **Saving settings no longer shows a "Settings saved" toast** - Save greying
  out says it; a problem still shows one.

- **The terminal list works like a list**: a click or the arrows show a terminal
  and keep the keyboard in the list (outlined in blue) - **Del** closes it, **F2**
  renames it (and the keyboard stays in the list), **Enter** or a second click
  goes in to type. Drag a terminal, or **Alt+↑** / **Alt+↓**, to reorder them.
- **Ctrl+`** now opens the panel on the Terminal tab with the keyboard in it
  (again in the terminal: hides the panel), as in VS Code; **Ctrl+J** shows or
  hides the panel.
- **Closing DNN Manager with unsaved settings no longer asks** - they are kept for
  the next start. It still asks when a changed password isn't saved, as passwords
  aren't kept.

- **Settings → About → Update** follows the update as it happens (*Checking for
  updates…*, *Update available*, *Downloading update…*, *Installing update…*,
  *Restarting…*). It no longer shows the release page's address, and *Unable to
  reach GitHub* has a gray dot instead of an orange one.
- Releases are built, tested and published by GitHub Actions when a `vX.Y.Z`
  tag is pushed; the version comes from the tag (see `docs/releasing.md`).
- The notes of every GitHub release are in `docs/release-notes`, one file per
  version; the release workflow publishes that file as the release's notes
  when it exists.
- `build.ps1 -Version X.Y.Z` builds the installer for a version other than
  `<Version>` in `DnnManager.csproj`.

### Fixed

- **An operation that asks first no longer shows before you answer** - Remove,
  Stop / Restart IIS and Clear cache showed as running (Output tab, status bar)
  while their question was open, and a No logged *Aborted by user* as an error.
  They now show once they start; a No leaves no trace, in the Output tab or the
  log file.
- **The Output tab's last line could end half under the panel's edge** - the result
  line (SUCCESS, its link) is laid out a moment after it is added, and the tab
  had already scrolled. While the end is in view it now stays in view, also when
  the panel gets lower, with room under the last line to scroll to.
- **A maximized window lost a few pixels at the screen edges.** It kept in
  only the resize border of the frame Windows still gives it, not the padded
  border around it; it now keeps in what the window really reaches past the
  screen, at any scaling.

## v1.6.0

**See what a site really is**: a site's Details read IIS, its folder,
`web.config`, `bin` and its database and mark every problem they find, and the
Output tab shows each operation as a pipeline of stages. DNN Manager is lighter
while it runs and safer around your data: folder sizes are measured only when
shown, its own warnings reach the log file, a database on another server is
never dropped, and the SQL container is reachable from this PC only.

### Upgrading from v1.5.x

- Install over v1.5.x as usual - the settings keep their format (version 3).
- Run **Settings → Docker container → Set up docker-compose** once to publish
  the SQL container on this PC only; the databases stay, they're in the volume.
- A `settings.json` whose projects folder is a drive or a system folder, or
  whose collation isn't a collation name, is now refused at start with the
  settings dialog - pick a folder of its own.
- **Remove…** no longer drops a database on another server than this PC; it
  says it is kept.
- **Reset to defaults** moved from Settings → General to **Troubleshoot**.
- The documentation moved: settings are in `docs/configuration.md`, the
  architecture in `docs/architecture.md`, and there are new pages for testing,
  releasing, troubleshooting and security.

### Changed

- **The Output tab is a pipeline** - the newest operation on top (status,
  title, what it works with, warnings or errors, times, duration - counting up
  while it runs), its stages on the left (done, warning, failed, running,
  still to come, skipped) with the sites kept warm and the operation's summary
  under them (narrow at first - drag its edge to resize it), and on the right
  every operation grouped by stage: each line with its time, values picked out,
  file paths shortened, warnings and errors with a WARN / ERROR label - an error
  with what lies behind it and what to do -, and a line at the end: SUCCESS
  with what it did and the site's address, ERROR with where it stopped, or
  CANCELLED. A red dot on **Output** says the last operation failed. Keep
  warm's messages moved out of it, to its *Background* list and the log file.
  Still read-only, selectable, copyable and searchable; **Clear** keeps an
  operation that is running. Clone and New project name their stages up front,
  so those still to come and those skipped after a failure show too.
- **The documentation is split by reader** - users: the user guide,
  [configuration](.docs/configuration.md) (every `settings.json` key) and
  [troubleshooting](.docs/troubleshooting.md); developers:
  [development](.docs/development.md), [architecture](.docs/architecture.md) (with
  Mermaid diagrams of the layers, an operation and the live updates),
  [testing](.docs/testing.md), [releasing](.docs/releasing.md) and
  [security](.docs/security.md).
- **A site's Details show what was detected, as an environment inspector** -
  every tab read from where it is: IIS and the app pool (bindings with their
  certificate and when it expires, SNI, the app pool's identity, recycling,
  limits and worker processes), the folder (whether the app pool can write to
  it), `web.config` (debug, custom errors, limits, the effective upload size,
  DNN's providers), `bin` (duplicate assemblies, binding redirects to a version
  that isn't there, missing references), DNN's database (portals, extensions,
  counts) and SQL Server (version, collation - marked when the database's
  differs -, recovery model, files). Each card says where its values come from; what is wrong or
  unusual is marked, with why, and gathered under **Detected issues**. No
  passwords or keys, no new buttons.
- **Settings → About** shows the version, commit, build date, program folder,
  release channel, whether GitHub has a newer release, license, repository and
  documentation, what DNN Manager runs on (.NET, architecture, Windows,
  Administrator) and what it works with (IIS, .NET Framework, Docker, its IIS
  and SQL libraries) - above the folders with your files.
- **Troubleshoot takes the whole window, like Settings** - no sidebar, its
  title with a **✕** that goes back to the page it was opened from.
- **Reset settings to defaults moved to Troubleshoot** (above *Reset to factory
  defaults*) - from Settings → General. It applies at once, with no Save step
  and no restart; the old `settings.json` is kept in `backups`.
- **The Projects tab shows each site as it is now** - its database, server and
  authentication from the site's `web.config`, the DNN version from its files,
  portals and host accounts from its database, folder, bindings and app pool
  from IIS - never what DNN Manager's settings would make of it. A DNN site
  whose `web.config` is missing, can't be read, has no `SiteSqlServer` or still
  has DNN's own connection shows its database as *not set*, with why, instead
  of the name the settings would give it. The overview reads the database with
  the connection `web.config` has (Windows authentication too - no longer the
  local container as `sa` in its place), follows changes made outside DNN
  Manager while it is open, and no longer shows the host account saved at
  setup when the database can't be read. SSMS's *Project* entry opens the
  `web.config` database, and is greyed out when there is none.
- **SQL (Live / Offline) is asked with the site's own connection** - the server
  and login its `web.config` has (Windows authentication too), for every site,
  also those on another SQL Server (they showed *External*). It was the local
  container from **Settings → Database server**, so a wrong host there showed
  every site *Offline* while they ran. The tooltip says what was asked and what
  it answered. A cold site's warm-up (keep warm) waits for that server too.

- **The log file reads as Markdown** - `# operation`, `## stage`, then each
  line with its time and no symbol in front; warnings and errors marked
  `[warning]` / `[error]`, an error's details and hint indented under it, and how
  the operation ended (finished in…, failed after…, cancelled).
- **Settings → Database server → Local SQL container has a Username field**
  (`sqlServer.userName` - the same setting as the SQL Server login; `sa` when
  empty): the login DNN
  Manager signs in to the container with - for the projects' databases, the
  SQL checks, clones, imports and SSMS's *Default* entry. **Set up
  docker-compose** still creates the container with `sa` and the password.
- **New project has a Database card again** - filled in from Settings →
  Database server (connection type, server, authentication, username,
  password) with the database named like the project; change it there for
  that project only, or **Use the settings** to fill it in again. Settings
  stay as they are.
- **Remove… asks once, and the database always goes with the project** - no
  separate *Also drop the database?* question; the confirmation names what is
  deleted: the IIS site, the folder and the database (its server too). A
  database on another server than this PC (a shared or staging SQL Server the
  site's `web.config` points at) is kept, and the confirmation says so - only
  databases here (the container, LocalDB, a local SQL Server) are dropped.
- **The overview's Keep warm card no longer has *This site's own values*** -
  every site is kept warm with the interval and pages from Settings → Projects
  → Keep warm.
- **The overview's Test connection button is gone** - the *SQL* line checks the
  database with the site's `web.config` connection by itself.
- **Output tab**: the stage rail's *Background* note is one line, cut short when
  the rail is narrow; the log keeps room for its scrollbar, so nothing runs
  under it; more room under each operation's result bar. Its header and stage
  rail have the panel's font and colours (like the Terminal tab's list) - only
  the log itself is monospaced. The log's times read 14:37:44 (no tenths) in
  a brighter colour; a stage's duration has two decimals (`0.25s`). It keeps
  the newest 50 operations (all of them stay in the log file).
- **Clone, Back up, Set up existing project and Import say when they are done** -
  a message, with *Open folder* for a backup. Starting something while another
  operation runs says so in a message instead of a dialog.
- **Questions before something that can't be taken back look like it** -
  removing a project, dropping or replacing a database, cleaning up data and
  the resets: the button is red and Enter picks *Cancel*.
- **Esc closes Settings and Troubleshoot** (Settings' search is emptied first).
  Icon buttons have names for screen readers. The sidebar turns to icons at the
  same layout width whatever the UI scale.
- **Folder sizes are only measured while they are shown** - the Projects
  table's *Size* column, or a site's Details (that site only). They were walked
  - every file of every site - at start, after every operation and every ten
  minutes, also when nobody saw them.
- **Lighter while running**: the sites' memory is read without a snapshot of
  every process on the PC every 2 seconds; a site's Details read again only
  what changed (a start or stop reads IIS, not the site's folder and bin); the
  log list of a site is found off the UI thread; a terminal printing a lot no
  longer shifts its whole scrollback for every line. A site's right-click menu
  opens at once: *View logs* and *Open with* read the site's logs, solution and
  database only when they are opened.
- **Warnings and errors of DNN Manager itself go to the log file** - with their
  stack traces, for finding out what went wrong (`[warning]`, `[error]`,
  `[critical]` - the same one at most once in ten minutes). They were written
  nowhere.

### Security

- **The SQL container is published on this PC only** (`127.0.0.1:1433`) when
  DNN Manager reaches it as localhost - the default: the `sa` login and its
  well-known default password are no longer offered to the network. Another host
  in Settings → Database server keeps it on every network interface. Applies the
  next time **Set up docker-compose** runs.
- **Restoring a .bak quotes its file names** - a backup's logical file names
  (the backup's own data) went into the RESTORE statement as they were. The
  collation setting must be a collation name, and DNN's table prefix read from a
  database must be one, before either goes into SQL.
- **Clean up data and Clear website cache don't follow junctions** - a junction
  in a backup or cache folder could make them delete files elsewhere.
- **Removing a site leaves an app pool other sites use** - and its profile.
- **web.config is written whole or not at all** - to a file next to it that
  then takes its place, keeping its permissions; a `configSource` outside the
  site's folder is not read or written.
- **Links open in the browser as you, not as Administrator** (through Explorer).
- **The projects folder can't be a drive or a system folder** (Windows, Program
  Files, your user folder): every site in it would count as DNN Manager's, and
  removing one deletes its folder.

### Fixed

- **The log file stopped after Troubleshoot → Clean up data** deleted the logs
  while DNN Manager ran: it went on writing into the deleted file until the
  next start. The next line now starts the day's file again, and a write that
  fails is tried again a minute later instead of never.

## v1.5.0

**Sites stay fast**: keep warm stops IIS from shutting an idle DNN site down, so
its next page opens at once instead of after DNN starting up again. The
database server is one setting, a **Troubleshoot** page restarts or resets DNN
Manager, and **Cancel** undoes what an operation had already done.

### Upgrading from v1.4.x

- Install over v1.4.x as usual. The settings are upgraded to version 3 on the
  first start (see *Database profiles are gone* below); the new keys
  (`projects.keepWarm`, `appearance.uiScale`, `appearance.fontSize`) are added
  with their defaults.
- The **Environment** page moved into **Settings**: **Docker container**,
  **Database server** and **IIS** each end with a **Test and set up** card.
- **Clone project** is now **Clone…** on a project's right-click menu.

### Added

- **Keep warm** - the flame in a site's Actions (also on its right-click menu and
  its overview's **IIS** tab) keeps the site warm while DNN Manager runs,
  minimized too, so its next page opens at once instead of after DNN starting
  up again (3-10 s). IIS shuts an idle site's worker process down after its
  idle time-out (20 minutes by default): DNN Manager requests DNN's own
  `KeepAlive.aspx` before that - every 5 minutes, sooner for a shorter idle
  time-out, not while the site is in use - and warms the site up (its home page)
  as soon as its worker process is gone after a recycle, a crash or IIS starting.
  No browser and nothing changed in IIS. It holds back while the site or IIS is
  stopped, an operation runs on it, a debugger is attached (Visual Studio's
  managed attach too) or the site's SQL container is down; a site that keeps
  failing or crashing is left alone until **Check now**. Requests never go to
  DNN's installer, however a page or a redirect is written. The
  flame shows how it is going (its tooltip: *Enable keep warm* / *Disable keep
  warm*); the overview shows the idle time-out, interval
  and pages, and gives a site its own. Defaults in **Settings → Projects → Keep
  warm** (`projects.keepWarm`). Plain HTTP requests: switching it on asks
  nothing, and needs no mail server, extension or change in IIS or DNN.
- **Troubleshoot** (the bug next to the gear) - **Restart** restarts DNN
  Manager; **Clean up data** deletes what is ticked from `Documents\DnnManager`,
  each with its size (logs, kept DNN packages, settings copies and - never
  ticked for you - project backups); **Reset to factory defaults** puts DNN
  Manager back as it was installed. Projects - their IIS sites, folders and
  databases - are never touched.
- **Cancel puts everything back** - what the operation had made is taken away
  again, the last first (the IIS site and app pool, the database, files and
  folders, an edited `web.config`), each step shown in **Output**; what can't
  be put back is named there.
- **UI scale** (80-175 %) and **font size** (11-18 px) in **Settings →
  General**, applied at once.
- **Open with…** lists the installed editors and, in the same submenu, each
  installed SQL Server Management Studio.

### Changed

- **The database server is a setting, not a choice on New project.** Settings
  → **Database server** (was *SQL Server*) has the **connection type** new
  projects get their database on: the *Local SQL container (Docker)*, *SQL
  Server / SQL Server Express* (server, Windows or SQL Server authentication,
  login) or a *SQL Server Express LocalDB (file)*. New project has no Database
  card any more: the database is named like the project and tested on that
  server before anything is created.
- **Database profiles are gone** - and **Save as profile…** with them. The
  settings are upgraded to version 3 on the first start: the profile new
  projects started with becomes the database server (`sqlServer.type`,
  `server`, `authentication`, `userName`), its password moves to
  `DnnManager/database-server/password` in the Windows Credential Manager, and
  `projects.databaseProfiles` / `projects.defaultDatabaseProfile` are removed
  with the other profiles' passwords. Existing projects keep their databases.
- **Efficiency mode also while the window can't be seen** - covered by other
  windows, on another virtual desktop or behind the lock screen, not only
  minimized - and it is Windows' own *Efficiency mode* (the leaf in Task
  Manager). Terminal shells still start at Normal priority.
- **Bottom panel** - long Output lines wrap at the panel's width; copy and paste
  are **Ctrl+C** / **Ctrl+V** and the right-click menu on every tab.
- The Projects table shows the **URL** column by default; the Actions column is
  tighter.
- **The README is short** - what DNN Manager is, a quick start and how to build
  it; the details moved to `docs/user-guide.md` and `docs/development.md`.

### Removed

- The **Environment** and **Clone project** pages - moved into Settings and the
  project menu.

## v1.4.0

**New project installs DNN for you**: the first visit shows your new site, signed
in as the host account you chose - no installation wizard - on the local SQL
container, SQL Server / SQL Server Express or a LocalDB file, tested before
anything is created. The bottom panel works like VS Code's (Output, Logs,
Terminal, search), a site opens in an overview with tabs, and the app is light
on resources while minimized.

### Upgrading from v1.3.x

- Install over v1.3.x as usual - settings are kept; the new keys
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
  manual setup leaves DNN's wizard. See *Tests* in docs/development.md.

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

## v1.3.0

DNN Manager now works like Docker Desktop: **Projects** is a live server table
that follows IIS and the projects folder by itself - no Refresh -, a status bar
shows IIS and this PC's resources, and a terminal panel holds the activity log
next to real terminals. Settings is a page of its own and applies without a
restart, and every button, input and switch shares one look.

### Upgrading from v1.2.x

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
  seconds. How it works is in docs/development.md under *Live updates*.
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
  *Upgrading from v1.2.x*).
- **One look for every control.** Text boxes and password boxes now have
  rounded corners like the buttons, selects and search boxes; buttons, text
  boxes and selects share one height (30 px) so they line up side by side; the
  right-click menus and select lists are rounded too, and a select shows the
  accent colour when it has the keyboard. A select with nothing to choose from
  (e.g. Clone's source project when the projects folder is empty) is greyed
  out. The styles are reusable: one file per
  kind of control in `Themes/Controls`, sharing sizes from `Themes/Tokens.xaml`
  (see *Control styles* in docs/development.md).
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

## v1.2.0

DNN Manager now has a Windows installer, and your settings and project backups
move into `Documents\DnnManager`, apart from the program and the sites, where
updates, reinstalls and uninstalls leave them alone.

### Upgrading from v1.1.x

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

## v1.1.0

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

## v1.0.3

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

## v1.0.2

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
