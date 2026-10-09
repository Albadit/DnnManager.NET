# Security

DNN Manager runs as Administrator and creates, changes and deletes IIS sites,
databases and folders. This page says what it is allowed to touch, where its
secrets are, what it exposes, and the risks that are known and still open. The
coding rules that keep it that way are in
[development.md](development.md#conventions).

## Administrator rights

- **Why:** IIS (`Microsoft.Web.Administration`), app pool identities and folder
  permissions need them. `AdminElevation` relaunches the app elevated through
  UAC when it starts without them.
- **What runs elevated:** DNN Manager itself and the tools it starts: `docker`,
  `dotnet`, `winget`, `sqlcmd` (in the container), SqlPackage, PowerShell (IIS
  features), `schtasks`, and the embedded terminals (PowerShell, Command
  Prompt, Git Bash).
- **Only programs administrators alone can change run elevated.** A program in
  a folder your account can write - your PATH, `%LOCALAPPDATA%`, a per-user
  install - could be swapped by any program you run, which would then run as
  Administrator. So `ProcessRunner` looks a program up on PATH and takes the
  first copy whose file and folder only Administrators, SYSTEM and
  TrustedInstaller can change, and refuses the rest with a message saying why
  ([`Processes/TrustedPrograms.cs`](../src/DnnManager.Infrastructure/Processes/TrustedPrograms.cs)).
  `winget` is taken from `Program Files\WindowsApps`, not your WindowsApps
  link. A terminal is offered only for a shell installed that way (PowerShell 7
  and Git for Windows installed *for all users*).
- **SqlPackage** is installed by DNN Manager into its own tools folder
  (`%ProgramData%\DnnManager\tools`, below) - never a global .NET tool in
  `~\.dotnet\tools`, which is yours to change.
- **What runs as you, not elevated:** web addresses, folders and files it opens
  are handed to Explorer ([`Shell.cs`](../src/DnnManager.Presentation/Shell.cs)),
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
  (or a link) isn't used - one is made beside it.
- **The projects folder** (`C:\DNN`) is kept to SYSTEM, Administrators and you:
  a folder at the root of C: inherits *Authenticated Users: Modify*, which would
  let every account on the PC read each site's `web.config` (its database
  password) and change its `bin` folder. DNN Manager sets it when it starts and
  when the projects folder changes
  ([`Files/ProjectsFolderGuard.cs`](../src/DnnManager.Infrastructure/Files/ProjectsFolderGuard.cs)).
  Each site's folder keeps the rights DNN Manager gives IIS on it: its own app
  pool may change it; `IIS_IUSRS` and `IUSR` - every site's pool - may only read
  it (sites made by an earlier DNN Manager still give them Full control).
- **The hosts file** (`C:\Windows\System32\drivers\etc\hosts`): DNN Manager
  keeps one block of it - the host names of the IIS sites, to `127.0.0.1` or
  the address their binding listens on - and leaves every other line as it is
  ([`Hosts/HostsFile.cs`](../src/DnnManager.Infrastructure/Hosts/HostsFile.cs)).
  Before each change it copies the file to `hosts.dnnmanager.bak` beside it.
  A site bound to a real domain therefore opens locally on this PC instead of
  the live site - see
  [Host names and working offline](user-guide.md#host-names-and-working-offline).
- **Start at sign-in** is a scheduled task that runs `DnnManager.exe` with the
  highest rights at sign-in, without a UAC prompt
  ([`StartupTask.cs`](../src/DnnManager.Infrastructure/Startup/StartupTask.cs)).
  See the open risk below.

## Updates

An update is downloaded from the GitHub release and checked against GitHub's
own SHA-256 of the file (its `digest`) and its version; a release that lists no
SHA-256 isn't installed. It is downloaded into the admin-only temporary folder
above, and the helper that installs it - run elevated, from there - checks the
file's size and SHA-256 again right before it runs or copies it, holding it
open so nothing can change it meanwhile
([`Updates/UpdateHelper.cs`](../src/DnnManager.Infrastructure/Updates/UpdateHelper.cs)).
Setup's own download checks the SHA-256 the same way. What it doesn't check yet
is who made the file - the exes aren't signed (see the open risks).

## Secrets

| Secret | Where it is kept |
|---|---|
| The SQL container's `sa` password | The settings (the `settings` table in `Documents\DnnManager\dnnmanager.db`) → the row `sqlServer.saPassword`, encrypted for your Windows account (DPAPI, `dpapi:…`). A new installation makes up its own (24 characters); resetting the settings keeps it |
| The default DNN host password for new projects | Windows Credential Manager, `DnnManager/dnn-defaults/host-password` - none until you set one: New project asks for it |
| The database server login's password (**Settings → Database server**, SQL authentication) | Windows Credential Manager, `DnnManager/database-server/password` |
| A site's database login | The site's own `web.config` (`SiteSqlServer`), as DNN needs it. A site on the local container made by New project (automatic), Clone, Import or Host project signs in with a login of its own - `dnn_<project>`, owner of its database only, a new password each time - not with `sa` |
| A live server's connection string (**Export for deployment**) | The package's `web.config`, in `Documents\DnnManager\deployments\` - the Output tab says so; **Troubleshoot → Clean up data → Deployment packages** deletes them |
| A database password put on the clipboard (**Open in SSMS** when SSMS can't be signed in) | The clipboard, for 60 seconds - left out of Windows' clipboard history and cloud clipboard, and marked for clipboard managers to ignore; then taken off again unless something else was copied ([`SecretClipboard.cs`](../src/DnnManager.Presentation/Services/SecretClipboard.cs)) |

They never go into the Output tab, the log file or an error message (the tests
check that no password appears in any message or file; SqlPackage's output is
masked before it is logged), and the docker-compose file gets the `sa` password
on standard input - it is never written to disk. Settings' **Show
docker-compose.yml** shows a placeholder instead.

## What DNN Manager deletes - and its limits

| Action | What goes | Guard |
|---|---|---|
| **Remove…** | The IIS site; its app pool (and the pool's Windows profile) | A pool other sites still use is kept |
| | The site's folder | Only a folder in the projects folder - which can't be a drive or a system folder |
| | The site's database, and its own login (`dnn_<project>`) | Only a database on this PC (the container, LocalDB, a local SQL Server) that no other IIS site's `web.config` names; one on another server, or another site's too, is kept and named in the confirmation. A drop that fails is named in the result |
| | Its backups | Only when you answer *Delete backups* to the second question - *Keep backups* is the default |
| **New project / Clone / Import / Host project** | An IIS site of the project's name | Only one that serves the project's own folder: a site of that name serving another folder is refused, before anything is made |
| **Clone / Host project** (*Replace database*) | The database of the project's name | Asked first. Clone refuses a database another IIS site uses, and puts the copy in under a name of its own, swapped in only once it is complete; Host project gives the project a database of its own instead of one another site uses |
| **Troubleshoot → Clean up data** | What you tick in `Documents\DnnManager` (logs, packages - with the DNN versions saved in `dnnmanager.db` -, backups, deployment packages) | Never through a junction or link |
| **Clear website cache** | DNN's cache folders in the site | Never through a junction or link |
| **Cancel**, or a **failure**, of an operation | What the operation itself made so far | Undo only removes what it noted making; a failed DNN installation is left as it is to look into |

Destructive confirmations have a red button, and Enter picks *Cancel*.
`web.config` is written to a file next to it that then replaces it, so a crash
can't leave half a file; a `configSource` outside the site's folder is not read
or written. `sqlcmd` runs with `-x -X`: a database name read from a site's
`web.config` or a backup can't use sqlcmd's variables or `:!!` commands.

## Network exposure

- **The SQL container** is published on `127.0.0.1` only when
  `sqlServer.host` is `localhost` (the default) - from the next **Set up
  docker-compose**. With another host it is published on every network
  interface, with the `sa` login.
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
  release. Anyone with the publisher's GitHub credential could publish one.
  Signing (Authenticode) and checking the signature before an update runs needs
  a code-signing certificate or service; `build.ps1` and `DnnManager.iss`
  (`SignTool`) are where it goes.
- **Start at sign-in with a per-user install.** The default installer puts DNN
  Manager in `%LOCALAPPDATA%\Programs\DnnManager`, which your account can write
  without elevation. With start at sign-in on, a program running as you could
  replace `DnnManager.exe` and get Administrator rights at the next sign-in.
  Install for all users (`Setup.exe /ALLUSERS`, into Program Files) when you
  use start at sign-in.
- **Passwords on a command line.** `sqlcmd` in the container gets the `sa`
  password as `-P`, and a site login's password in its query; SqlPackage gets
  the source's and the target's connection strings (with their passwords) as
  arguments. They are visible to administrators of this PC, and to programs
  of yours, while those run.
- **Server certificates aren't checked** (`TrustServerCertificate=true`) - also
  for a remote or Azure SQL source when cloning or backing up, where a
  man-in-the-middle could read the login.
- **Embedded terminals run as Administrator** - what you type in them does too.
