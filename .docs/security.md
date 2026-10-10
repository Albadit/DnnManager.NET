# Security

DNN Manager runs as Administrator and creates, changes and deletes IIS sites,
databases and folders. This page says what it is allowed to touch, where its
secrets are, what it exposes, and the risks that are known and still open. The
coding rules that keep it that way are in
[development.md](development.md#conventions).

## Administrator rights

- **Why:** IIS (`Microsoft.Web.Administration`), app pool identities and folder
  permissions need them. `AdminElevation` relaunches the app elevated through
  UAC when it starts without them - through the launcher when there is one
  (below).
- **What runs elevated:** DNN Manager itself and the tools it starts: `docker`,
  `dotnet`, `winget`, `sqlcmd` (in the container), SqlPackage, PowerShell (IIS
  features), `schtasks`, SSMS, `vswhere` - and an **Administrator terminal**
  when you open one. The embedded terminals themselves run as you, without
  those rights (below).
- **How it is enforced:** a few choke points, each held by tests, rather than a
  check at every call site - [ADR 0004](adr/0004-elevation-boundary.md).
- **What your account can change is untrusted input.** DNN Manager runs with
  Administrator rights, but its settings (`Documents\DnnManager\dnnmanager.db`),
  your environment variables, `%TEMP%`, Documents and the projects folder can
  be changed by any program you run - and a site's folder by its app pool. What
  it takes from there is checked as if it came from someone else:
  - The settings and `DNNMANAGER_*` environment variables are held to the same
    rules
    ([`Configuration/SettingRules.cs`](../src/DnnManager.Application/Configuration/SettingRules.cs)):
    the projects folder can't be a drive, a system folder or anything in one
    (Windows, Program Files, ProgramData), Windows' own folders at the top of
    a drive (`$Recycle.Bin`, `System Volume Information`, `Recovery`, …),
    `C:\Users` itself, your user folder itself, another user's folder,
    `AppData\Local\Programs`, `AppData\Local\Microsoft\WindowsApps` or DNN
    Manager's own data folder
    (`Documents\DnnManager`); DNN release sources must be https; the hostname
    suffix is a host name (RFC 1123 labels, not an IP address) and the
    collation a collation name - both go into SQL; Docker's container and
    volume names are names Docker allows (`^[a-zA-Z0-9][a-zA-Z0-9_.-]*$` - they
    go on docker's command line); an IIS feature's name is a Windows feature
    name only (letters, digits, `-`, `_`, `.`) - and reaches PowerShell as
    data, never as part of its script. A saved value that breaks a rule goes
    back to its default at the start, with a warning naming it; environment
    variables that break a rule are all ignored, with a warning
    ([configuration.md](configuration.md#when-the-settings-cant-be-used)).
  - The command line of an unelevated start isn't passed on to the elevated
    one, and none is read as settings.
  - .NET startup hooks (`DOTNET_STARTUP_HOOKS`) are off in what is shipped
    (`StartupHookSupport` in [`DnnManager.csproj`](../DnnManager.csproj)).
    Other variables that make .NET load code or write a diagnostics file as it
    starts (`CORECLR_PROFILER*`, `DOTNET_DiagnosticPorts`,
    `DOTNET_EventPipeOutputPath`, …) are read by .NET before DNN Manager's
    own code runs - so an installed DNN Manager is started through a
    launcher that isn't .NET's runtime (next point). DNN Manager warns at the
    start (a toast and the Output tab) when it started with one anyway, and
    doesn't pass them on (below).
  - **The launcher**, `DnnManager-launcher.exe` beside `DnnManager.exe`
    ([`src/DnnManager.Launcher`](../src/DnnManager.Launcher/DnnManager.Launcher.csproj),
    [`Startup/LaunchEnvironment.cs`](../src/DnnManager.Infrastructure/Startup/LaunchEnvironment.cs)),
    is what the sign-in task starts and what Windows' administrator prompt
    starts when DNN Manager relaunches itself elevated. It is compiled ahead
    of time (Native AOT, with `EventSourceSupport` off): a native exe without
    CoreCLR, so it has no profiling API to load a profiler with and no
    EventPipe or diagnostic port
    ([Microsoft's notes](https://learn.microsoft.com/dotnet/core/deploying/native-aot/diagnostics)).
    It drops every `DOTNET_*`, `COMPlus_*`, `COR_*` and `CORECLR_*` variable,
    sets `DOTNET_EnableDiagnostics=0`, and starts `DnnManager.exe` from its own
    folder with no arguments. Only Setup installs it; the portable exe has
    none and starts as before (see the open risks).
  - A site's `web.config` names its own database server - and the site's app
    pool can change it. With Windows authentication the monitor asks only a
    server on this PC, or the SQL Server chosen in **Settings → Database
    server**, signing in as you; any other it leaves unasked, rather than
    send your Windows sign-in every few seconds to a server the site chose
    ([`Monitoring/ServerStateMonitor.cs`](../src/DnnManager.Infrastructure/Monitoring/ServerStateMonitor.cs)).
    A pipe's path alone (`\\server\pipe\…`) is read as that server, and only
    this PC's own name and addresses - not any name starting with it - count
    as this PC
    ([`SqlServerAddress.cs`](../src/DnnManager.Application/Abstractions/SqlServerAddress.cs),
    which reads this PC's addresses once and again when they change). What
    you start yourself asks first: **Open in SSMS**, portal-alias edits,
    **Upgrade DNN** and **Restore backup** name the server and ask before
    signing in to one that is neither on this PC nor the one in Settings
    (`SqlServerAddress.MaySignInAsUser` / `SignInQuestion`).
  - The database a site's `web.config` names can be changed by its app pool
    too: **Remove…** and portal-alias edits change or drop a database that
    isn't the project's own (named like it) only after a question naming both.
- **Junctions made without administrator rights aren't followed.** At its start
  DNN Manager turns on Windows' redirection trust for itself
  ([`Files/RedirectionTrust.cs`](../src/DnnManager.Infrastructure/Files/RedirectionTrust.cs)):
  a junction a program without administrator rights made - in a site's folder,
  `%TEMP%`, Documents - fails to open ("untrusted mount point") instead of
  taking a delete, a write or a permission change wherever it points. A
  junction an administrator made still works. A Windows without redirection
  trust is told to run Windows Update, with a warning at the start. On top of
  that, every place that deletes or writes in a folder others can write checks
  for links and junctions on the way itself, through two classes only:
  - [`SafePath`](../src/DnnManager.Application/SafePath.cs) - `IsSameOrInside`
    and `IsInside` (the one place that decides whether a path is in a folder),
    `Under`, `HasLink`, `IsLink`, `IsHardLinked`, `DeleteTree` (a junction is
    unlinked, never followed) and `LongPath`;
  - [`SafeZip`](../src/DnnManager.Infrastructure/Files/SafeZip.cs) - `Target`,
    `Write`, `ExtractEntry` and `EnsureFits` for every zip unpacked (a backup,
    a DNN package): an entry lands strictly inside the folder or not at all,
    a name with `:` (another file's stream, `web.config::$DATA`) is refused,
    the files to leave alone (`web.config`, the database) are compared by
    their resolved full path (`./web.config`, `WEB~1.CON`), a file with other
    names (a hard link) is replaced rather than written into, and a package
    that would leave less than 512 MB free is refused before anything is
    written. A package with one unsafe path is refused as a whole.

  They cover unpacking a backup or a DNN package, **Clear website cache** (a
  site folder that is a link fails it), a clone's copy (a linked folder is
  skipped, with a warning), the install template, undo, **Clean up data**,
  deleting old backups, and the rights IIS gets on a site's folder (the whole
  path is checked). A refusal ends with a `Hint:` line saying what to do.
  Windows' **Developer Mode** lets programs without administrator rights make
  symbolic links; DNN Manager warns at the start while it is on.
- **A site's XML** (`web.config`, a `configSource` file, DNN's install
  template) is read without DTDs and without fetching anything
  ([`WebConfigs/SiteXml.cs`](../src/DnnManager.Infrastructure/WebConfigs/SiteXml.cs)) -
  its app pool can change it.
- **Only programs administrators alone can change run elevated.** A program in
  a folder your account can write - your PATH, `%LOCALAPPDATA%`, a per-user
  install - could be swapped by any program you run, which would then run as
  Administrator. So `ProcessRunner` looks a program up on the computer's PATH
  (not yours) and takes the first copy whose file, folder and every folder
  above only Administrators, SYSTEM and TrustedInstaller can change - and
  starts it by its own path, with no link or junction in it, so the file that
  was checked is the file that runs. The rest is refused with a message saying
  why (`TrustedPrograms.Resolve` in
  [`Processes/TrustedPrograms.cs`](../src/DnnManager.Infrastructure/Processes/TrustedPrograms.cs)).
  `winget` is taken from `Program Files\WindowsApps`, not your WindowsApps
  link; SSMS (greyed in the menu, with why, when refused), Explorer
  (`%SystemRoot%\explorer.exe`) and `docker compose`'s plugin are held to the
  same rule. There are two ways to start a program elevated:
  [`ProcessRunner`](../src/DnnManager.Infrastructure/Processes/ProcessRunner.cs)
  for a tool whose output is read, and
  [`ElevatedStart`](../src/DnnManager.Infrastructure/Processes/ElevatedStart.cs)
  for the rest - SSMS, `vswhere` (with a real time limit), Explorer and the
  IDE fallback. A test (`LayeringTests`) scans the source and fails on a
  `Process.Start` or `new ProcessStartInfo` anywhere else (DNN Manager's own
  restarts and the update helper are the listed exceptions). The programs
  `ProcessRunner` starts are in a Windows job object, ended with DNN Manager.
  What such a program inherits leaves out what would make it load other code
  ([`Processes/ChildEnvironment.cs`](../src/DnnManager.Infrastructure/Processes/ChildEnvironment.cs)):
  - every `DOTNET_*`, `COMPlus_*`, `COR_*`, `CORECLR_*`, `NUGET_*`, `DOCKER_*`
    and `SQLCMD*` variable is dropped, and `DOTNET_EnableDiagnostics=0` is set;
  - `SystemRoot`, `ProgramFiles`, `ProgramData` and the like are Windows' own
    folders, not what your environment says;
  - PATH is the computer's, its `%variables%` filled in from Windows' folders
    and the computer's own variables, never yours;
  - `PSModulePath` is the modules of Windows and Program Files only - Windows
    PowerShell would otherwise look in your Documents first, and load a module
    it finds there (PSReadLine, Dism) by itself;
  - docker reads a configuration folder of DNN Manager's own,
    `%ProgramData%\DnnManager\docker` (admin-only, empty), not your `.docker`:
    no context, plugin folder or credential helper of yours. Its compose
    plugin then comes from `%ProgramData%\Docker\cli-plugins` or Docker
    Desktop's own folder in Program Files, held to the rule above.

  One exception: **Docker Desktop installed for your account only** (its
  `docker` in `%LOCALAPPDATA%\Programs\DockerDesktop`, on your own PATH). Its
  `docker` isn't refused but run **as you, without administrator rights** - the
  terminals' token (below), inheriting its three pipes and nothing else, in the
  same job object
  ([`Processes/UserProcess.cs`](../src/DnnManager.Infrastructure/Processes/UserProcess.cs)).
  Swapped by a program of yours, it gets no more than that program has. It
  reads your own `.docker`, and its compose plugin isn't checked: it runs with
  your rights too. Backups are copied in and out of the container as a stream
  through `docker exec` that DNN Manager reads and writes itself - such a
  `docker` can't reach the admin-only temporary folder. Only `docker` is run
  this way (`ProcessRunner.RunsAsUser`): it needs no administrator rights.

  SSMS, an IDE started directly, and DNN Manager's own restarts (the update
  helper, Setup, the new version) start the same way. SqlPackage is installed
  only from nuget.org, with a NuGet.Config of DNN Manager's own - not yours.
- **The embedded terminals run as you, without administrator rights**: a
  restricted copy of DNN Manager's own token - the Administrators group
  deny-only, no privileges, medium integrity - as Windows makes your everyday
  token from the elevated one (`UnelevatedToken` in
  [`Terminal/PseudoConsole.cs`](../src/DnnManager.Infrastructure/Terminal/PseudoConsole.cs)).
  They run your own shells (a per-user PowerShell 7 or Git too) with your
  profile and PATH. When that token can't be made, no terminal starts - never
  an elevated one in its place.
- **An Administrator terminal** is asked for on its own (**New Administrator
  terminal**, the arrow beside **+**). It offers only shells installed for all
  users (PowerShell 7 and Git for Windows *for all users*; Command Prompt is
  always `System32\cmd.exe`) - one others could change is shown greyed, with
  why - and starts them without the scripts in your own folders that a shell
  runs as it starts: `-NoProfile`, `cmd /d` (no AutoRun), `bash --noprofile
  --norc`; Windows PowerShell finds only the modules of Windows and Program
  Files. It keeps your PATH. Its tab is titled *Administrator: …*, with a
  warning above it - what you type in it runs as Administrator.
- **SqlPackage** is installed by DNN Manager into its own tools folder
  (`%ProgramData%\DnnManager\tools`, below) - never a global .NET tool in
  `~\.dotnet\tools`, which is yours to change. Its version is pinned
  (`170.5.96`), and NuGet's package source mapping takes it from nuget.org
  only.
- **What runs as you, not elevated:** the embedded terminals (above); web
  addresses, folders and files it opens are handed to Explorer
  ([`Shell.cs`](../src/DnnManager.Presentation/Shell.cs), through
  `ElevatedStart.Explorer`),
  and **Open in VS Code / Visual Studio / Rider** starts the IDE through the
  desktop's shell ([`Unelevated.cs`](../src/DnnManager.Presentation/Unelevated.cs)) -
  an editor has no need of Administrator rights. Only when the shell can't
  start it, and only administrators can change the IDE, is it started directly.
- **DNN Manager's own temporary folder** is `%ProgramData%\DnnManager\temp`:
  owned by Administrators, only Administrators and SYSTEM may change it
  ([`Files/PrivateTemp.cs`](../src/DnnManager.Infrastructure/Files/PrivateTemp.cs)).
  What DNN Manager writes and later reads back or runs goes there, never to
  `%TEMP%`: the update and its helper, database exports and `.bak` copies made
  while cloning, deployment packages being made, downloaded DNN packages, and
  the sign-in task's definition. A folder of that name someone else made first
  (or a link) isn't used - an earlier admin-only `temp-…` beside it is used
  again, or a new one made. *OWNER RIGHTS* gets read and execute only, so the
  folder's owner can't change its rights either - also where Group Policy makes
  the user the owner of what an administrator makes. A failure to make it is
  tried again at the next use; files left there for more than 24 hours are
  deleted.
- **The projects folder** (`C:\DNN`) is kept to SYSTEM, Administrators and you:
  a folder at the root of C: inherits *Authenticated Users: Modify*, which would
  let every account on the PC read each site's `web.config` (its database
  password) and change its `bin` folder. DNN Manager sets it when it starts and
  when the projects folder changes
  ([`Files/ProjectsFolderGuard.cs`](../src/DnnManager.Infrastructure/Files/ProjectsFolderGuard.cs)).
  Each site's folder keeps the rights DNN Manager gives IIS on it: its own app
  pool may change it; `IIS_IUSRS` and `IUSR` - every site's pool - may only read
  it. Sites an earlier DNN Manager made gave all three Full control; that is
  lowered at the start. Since the setting is yours to change, the guard never
  gives you rights you didn't have: a folder that is already there is set to
  you only when it is yours (you own it); one an administrator made - as an
  earlier DNN Manager made `C:\DNN` - only loses everyone's groups, and only
  when every folder in it is a DNN site; never through a junction.
  While DNN installs, its install template - with the host password - is
  readable by the site's own app pool only, not by `IIS_IUSRS`.
- **The hosts file** (`C:\Windows\System32\drivers\etc\hosts`): DNN Manager
  keeps one block of it - the host names of the IIS sites, to `127.0.0.1` or
  the address their binding listens on - and leaves every other line as it is
  ([`Hosts/HostsFile.cs`](../src/DnnManager.Infrastructure/Hosts/HostsFile.cs)).
  Before each change it copies the file to `hosts.dnnmanager.bak` beside it.
  A site bound to a real domain therefore opens locally on this PC instead of
  the live site - see
  [Host names and working offline](user-guide.md#host-names-and-working-offline).
- **Start at sign-in** is a scheduled task that runs the launcher (or
  `DnnManager.exe`, when there is none) with the highest rights at sign-in,
  without a UAC prompt
  ([`StartupTask.cs`](../src/DnnManager.Infrastructure/Startup/StartupTask.cs)).
  It is made only when both are files only administrators can change (an
  installation for all users, in Program Files), and only starts at sign-in -
  not on demand, so no other program can start it with those rights. At each
  start a task an earlier version made to start `DnnManager.exe` directly is
  made again to start the launcher; one for a per-user installation is
  removed, with a warning - but only when the answer is definitely *others can
  change it*, not when the rights couldn't be read just then. Uninstalling
  removes the task too, asking for administrator rights when it must.
- **Installation:** Setup installs for all users, into Program Files, and asks
  for administrator rights when it starts. An installation for your account
  only (the default up to 1.8.1, or `Setup.exe /CURRENTUSER`) is updated where
  it is; Setup offers to move it to Program Files, and DNN Manager warns about
  it once per version - it runs with Administrator rights from a folder any
  program of yours can change. Setup passes on to a Setup it starts only
  fixed switches (`/Done` and `/Back` take only `Updated`, `Repaired` or
  `Uninstalled`), and starts its own exe again only while it is the file that
  started. Uninstalling lists what stays and asks - *No* by default - whether
  to remove `Documents\DnnManager`, `%ProgramData%\DnnManager` (an
  installation for all users only) and the passwords saved in the Credential
  Manager; Docker's container and volume, the projects, IIS and the hosts file
  are never touched.

## Updates

An update is downloaded from the GitHub release and checked against GitHub's
own SHA-256 of the file (its `digest`) and its version; a release that lists no
SHA-256 isn't installed. It is downloaded into the admin-only temporary folder
above, and the helper that installs it - run elevated, from there - checks the
file's size and SHA-256 again right before it runs or copies it, holding it
open so nothing can change it meanwhile
([`Updates/UpdateHelper.cs`](../src/DnnManager.Infrastructure/Updates/UpdateHelper.cs)).
Setup's own download checks the SHA-256 the same way, and when it hands over to
a newer Setup it runs that one from its own admin-only temporary folder, after
checking the SHA-256 again. The update helper copied into the admin-only folder
takes the exe and its DLLs from the installation - an installation for all
users, in Program Files, is admin-only itself. What isn't checked yet is who
made the file - the exes aren't signed (see the open risks); releases are built
by GitHub's release workflow, with a provenance attestation
([releasing.md](releasing.md)).

**DNN packages** are downloaded over https only (or from this computer), into
the admin-only temporary folder - not the project. A download GitHub redirects
to an address that isn't https is refused, and says so. When GitHub gives a
SHA-256 for the package (older releases have none), the download must match
it, and so must a kept package in `Documents\DnnManager\packages` each time it
is used - one that doesn't is deleted. The SHA-256s are saved with the version
list (`dnn_releases`: `sha256`, `upgrade_sha256`), so a kept package is checked
offline too. Unpacking goes through `SafeZip` (above).

## Secrets

| Secret | Where it is kept |
|---|---|
| The SQL container's `sa` password | The settings (the `settings` table in `Documents\DnnManager\dnnmanager.db`) → the row `sqlServer.saPassword`, encrypted for your Windows account (DPAPI, `dpapi:…`). A new installation makes up its own (24 characters); resetting the settings keeps it. The container has it in its environment (`MSSQL_SA_PASSWORD` - Microsoft's image reads it from nowhere else) |
| The default DNN host password for new projects | Windows Credential Manager, `DnnManager/dnn-defaults/host-password` - none until you set one: New project asks for it |
| The database server login's password (**Settings → Database server**, SQL authentication) | Windows Credential Manager, `DnnManager/database-server/password` |
| A site's database login | The site's own `web.config` (`SiteSqlServer`), as DNN needs it. A site on the local container made by New project (automatic), Clone, Import or Host project signs in with a login of its own - `dnn_<project>` (`dnn_<project>_2`, … when another site, one renamed since, signs in with that one), owner of its database only, a new password each time - not with `sa`. A login another site signs in with is never given a new password or dropped |
| A live server's connection string (**Export for deployment**) | The package's `web.config`, in `Documents\DnnManager\deployments\` - the Output tab says so; **Troubleshoot → Clean up data → Deployment packages** deletes them |
| A database password put on the clipboard (**Open in SSMS** when SSMS can't be signed in) | The clipboard, for 60 seconds - left out of Windows' clipboard history and cloud clipboard, and marked for clipboard managers to ignore; then taken off again unless something else was copied - and when DNN Manager quits ([`SecretClipboard.cs`](../src/DnnManager.Presentation/Services/SecretClipboard.cs)) |

They never go into the Output tab, the log file or an error message (the tests
check that no password appears in any message or file; SqlPackage's output is
masked before it is logged), and the docker-compose file gets the `sa` password
on standard input - it is never written to disk. Settings' **Show
docker-compose.yml** shows a placeholder instead. `sqlcmd` in the container
gets the `sa` password in `SQLCMDPASSWORD` and a site login's new password on
its standard input, and the container's health check reads `SQLCMDPASSWORD`
too - none on a command line. Every connection DNN Manager opens is built in
one place (`ConnectionStrings.For` in
[`Sql/ConnectionStrings.cs`](../src/DnnManager.Infrastructure/Sql/ConnectionStrings.cs)),
which decides when a server's certificate is checked; every name and value put
into SQL text is quoted by
[`Sql/SqlText.cs`](../src/DnnManager.Infrastructure/Sql/SqlText.cs).

## What DNN Manager deletes - and its limits

| Action | What goes | Guard |
|---|---|---|
| **Remove…** | The IIS site; its app pool (and the pool's Windows profile) | A pool other sites still use is kept |
| | The site's folder | Only a folder in the projects folder - which can't be a drive or a system folder. What is still in use is deleted by Windows at the next restart: first every folder in it is made Administrators' and SYSTEM's only, so nothing can put a junction on the way meanwhile, and a link in it is deleted itself, never followed |
| | The site's database, and its own login (`dnn_…`, unless another site signs in with it) | Only a database on this PC (the container, LocalDB, a local SQL Server) that no other IIS site's `web.config` names; one on another server, or another site's too, is kept and named in the confirmation. One that isn't the project's own (named like it) is dropped only after a question naming both. A drop that fails is named in the result |
| | Programs holding files in the folder | Closed only after you confirm - **Close and delete** isn't the default |
| | Its backups | Only when you answer *Delete backups* to the second question - *Keep backups* is the default |
| **At each start**, with `backups.keepDays` set | Project backups and deployment packages older than that | Off (0) by default; only the dated folders in `backups\<project>\` and `deployments\`, never through a junction or link |
| **New project / Clone / Import / Host project** | An IIS site of the project's name | Only one that serves the project's own folder: a site of that name serving another folder is refused, before anything is made |
| **Clone / Host project** (*Replace database*) | The database of the project's name | Asked first. Clone refuses a database another IIS site uses, and puts the copy in under a name of its own, swapped in only once it is complete; Host project gives the project a database of its own instead of one another site uses |
| **Troubleshoot → Clean up data** | What you tick in `Documents\DnnManager` (logs, packages - with the DNN versions saved in `dnnmanager.db` -, backups, deployment packages, old settings files) | Never through a junction or link |
| **Clear website cache** | DNN's cache folders in the site | Never through a junction or link; refused when the site's folder itself is one |
| **Cancel**, or a **failure**, of an operation | What the operation itself made so far | Undo only removes what it noted making; a failed DNN installation is left as it is to look into |

Destructive confirmations have a red button, and Enter picks *Cancel*.
`web.config` is written to a file next to it that then replaces it, so a crash
can't leave half a file; a `configSource` outside the site's folder is not read
or written. `sqlcmd` runs with `-x -X`: a database name read from a site's
`web.config` or a backup can't use sqlcmd's variables or `:!!` commands.

## Network exposure

- **The SQL container** is published on this PC's loopback addresses only -
  `127.0.0.1` and, with IPv6, `[::1]` (Windows resolves `localhost` to `::1`
  first) - when `sqlServer.host` is `localhost` (the default), from the next
  **Set up docker-compose**. With another host it is published on every network
  interface, with the `sa` login. Its image is pinned by digest
  (`mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04@sha256:4402d880…`,
  [`Docker/DockerComposeService.cs`](../src/DnnManager.Infrastructure/Docker/DockerComposeService.cs)),
  so a new SQL Server comes only with a new DNN Manager.
- **The IIS sites** are bound on every network interface (`*:<port>:<host>`),
  so others on the network can reach a site by its IP address.
- **What DNN Manager sends:** GitHub's release API is asked for DNN's releases
  and - a moment after DNN Manager starts, unless **Settings → General → Look
  for a newer DNN Manager when it starts** is off - for a newer DNN Manager.
  GitHub gets this PC's address and the user agent `DnnManager`, nothing else.
  Keep warm requests go to the sites on this PC. There is no telemetry.
- **Old installations' passwords:** an installation made by an earlier DNN
  Manager may still use `Admin@123` for the container's `sa` and for new
  projects' DNN host account, and the sites it made sign in as `sa`. Change both in
  **Settings → Database server** and **Settings → Projects** if the PC is on a
  network you don't trust.

## Known open risks

- **The exes aren't signed.** An update is checked against GitHub's SHA-256 of
  the file, which proves it is the file of the release - not who made the
  release. Releases are built by the release workflow with a provenance
  attestation, which shows where a file came from but isn't checked by DNN
  Manager. Signing is ready in the workflow (Azure Artifact Signing,
  [releasing.md](releasing.md#code-signing)) but stays off until its variables
  are set; after that, the update helper and Setup still need to check the
  signer. Whoever can push a `v*` tag starts a release; it is published only
  once a reviewer approves the `publish` environment - when the owner has set
  that up on GitHub ([Set up GitHub](releasing.md#set-up-github)).
- **A per-user installation** (made by 1.8.1 or older, or with
  `/CURRENTUSER`) runs with Administrator rights from
  `%LOCALAPPDATA%\Programs\DnnManager`, which any program of yours can change.
  DNN Manager warns about it and won't start at sign-in from there; Setup
  offers to move it to Program Files.
- **The portable exe has no launcher**, so started through UAC it gets your
  environment's .NET variables as DNN Manager up to 1.8.1 did (it warns when
  one is set). And as a single file it extracts its native DLLs (WPF's,
  SQLite's, SqlClient's - from SqlClient 6.1 on also MSAL's `msalruntime.dll`)
  into `%TEMP%\.net` and loads them from there with Administrator rights - a
  folder every program of yours can change. Accepted for the portable exe;
  use the installed DNN Manager, which has them beside it in Program Files.
- **Passwords where others may see them.** The container's `sa` password is in
  its environment (`MSSQL_SA_PASSWORD`: Microsoft's SQL Server image has no
  password file), which those allowed to use Docker can read
  (`docker inspect`). SqlPackage gets the source's and the target's connection
  strings (with their passwords) as arguments; elevated processes' command
  lines are readable by administrators of this PC.
- **Server certificates on this PC aren't checked** (the container, LocalDB, a
  local instance have self-signed ones). A SQL Server on another computer must
  show a certificate Windows trusts - for one with a self-signed certificate,
  import it into this PC's Trusted Root Certification Authorities.
- **An Administrator terminal** runs what you type with those rights, with your
  PATH. PowerShell 7 adds the modules folder in your Documents itself, whatever
  `PSModulePath` says, so a module put there (PSReadLine, which it loads as it
  starts) runs as Administrator in a PowerShell 7 Administrator terminal.
- **A terminal without administrator rights runs in DNN Manager's sign-in
  session**, the elevated one: drive letters mapped in your everyday session
  (a network drive) may be missing there, and the other way round.
- **Setup starts its own exe through `cmd`** before the installation has
  started (Inno Setup's `Exec` refuses its own exe until then): `cmd /d /v:off
  /c start`, with the path checked for quotes, `%`, `!` and control
  characters. Once the installation has started it runs the exe directly.
- **The sign job compiles Setup.** In the release workflow, the job that may
  sign in to Azure also runs Inno Setup (the pinned, hash-checked copy) - it
  restores no package and runs no test, but Inno's compiler runs there.
- **The single-instance names** (the mutex and events DNN Manager uses to find
  a running copy and ask it to quit) can be made first, or signalled, by any
  program in your session: that can keep DNN Manager from starting or make it
  quit - not gain anything.
- **A new site is reachable from the network while DNN installs** (its
  binding is `*:<port>:<host>`), like every site afterwards.
