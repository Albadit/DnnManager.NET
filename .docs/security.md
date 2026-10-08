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
  `sqlcmd` (in the container), SqlPackage, PowerShell (IIS features), `winget`,
  and the IDEs and terminals opened from it (VS Code shows *[Administrator]*).
- **What doesn't:** web addresses, folders and files it opens are handed to
  Explorer ([`Shell.cs`](../src/DnnManager.Presentation/Shell.cs)), so the
  browser runs as you.
- **The hosts file** (`C:\Windows\System32\drivers\etc\hosts`): DNN Manager
  keeps one block of it - the host names of the IIS sites, to `127.0.0.1` or
  the address their binding listens on - and leaves every other line as it is
  ([`Hosts/HostsFile.cs`](../src/DnnManager.Infrastructure/Hosts/HostsFile.cs)).
  A site bound to a real domain therefore opens locally on this PC instead of
  the live site - see
  [Host names and working offline](user-guide.md#host-names-and-working-offline).
- **Start at sign-in** is a scheduled task that runs `DnnManager.exe` with the
  highest rights at sign-in, without a UAC prompt
  ([`StartupTask.cs`](../src/DnnManager.Infrastructure/Startup/StartupTask.cs)).
  See the open risk below.

## Secrets

| Secret | Where it is kept |
|---|---|
| The SQL container's `sa` password | The settings (the `settings` table in `Documents\DnnManager\dnnmanager.db`) → the row `sqlServer.saPassword`, encrypted for your Windows account (DPAPI, `dpapi:…`) |
| The default DNN host password for new projects | Windows Credential Manager, `DnnManager/dnn-defaults/host-password` |
| The database server login's password (**Settings → Database server**, SQL authentication) | Windows Credential Manager, `DnnManager/database-server/password` |
| A site's database login | The site's own `web.config` (`SiteSqlServer`), as DNN needs it |

They never go into the Output tab, the log file or an error message (the tests
check that no password appears in any message or file), and the
docker-compose file gets the `sa` password on standard input - it is never
written to disk. Settings' **Show docker-compose.yml** shows a placeholder
instead.

## What DNN Manager deletes - and its limits

| Action | What goes | Guard |
|---|---|---|
| **Remove…** | The IIS site; its app pool (and the pool's Windows profile) | A pool other sites still use is kept |
| | The site's folder | Only a folder in the projects folder - which can't be a drive or a system folder |
| | The site's database | Only a database on this PC (the container, LocalDB, a local SQL Server); one on another server is kept and named in the confirmation |
| **Troubleshoot → Clean up data** | What you tick in `Documents\DnnManager` (logs, packages - with the DNN versions saved in `dnnmanager.db` -, backups) | Never through a junction or link |
| **Clear website cache** | DNN's cache folders in the site | Never through a junction or link |
| **Cancel** of an operation | What the operation itself made so far | Undo only removes what it noted making |

Destructive confirmations have a red button, and Enter picks *Cancel*.
`web.config` is written to a file next to it that then replaces it, so a crash
can't leave half a file; a `configSource` outside the site's folder is not read
or written.

## Network exposure

- **The SQL container** is published on `127.0.0.1` only when
  `sqlServer.host` is `localhost` (the default) - from the next **Set up
  docker-compose**. With another host it is published on every network
  interface, with the `sa` login.
- **The IIS sites** are bound on every network interface (`*:<port>:<host>`),
  so others on the network can reach a site by its IP address.
- **Default passwords** are public: `Admin@123` for the container's `sa` and
  for a new project's DNN host account until you set your own (**Settings →
  Database server** and **Settings → Projects**). Change both if the PC is on
  a network you don't trust.

## Known open risks

- **Start at sign-in with a per-user install.** The default installer puts DNN
  Manager in `%LOCALAPPDATA%\Programs\DnnManager`, which your account can write
  without elevation. With start at sign-in on, a program running as you could
  replace `DnnManager.exe` and get Administrator rights at the next sign-in.
  Install for all users (`Setup.exe /ALLUSERS`, into Program Files) when you
  use start at sign-in.
- **The `sa` password on a command line.** `sqlcmd` in the container gets it as
  `-P`, visible to other processes in the container and to administrators of
  this PC while it runs.
- **Server certificates aren't checked** (`TrustServerCertificate=true`) - also
  for a remote or Azure SQL source when cloning or backing up, where a
  man-in-the-middle could read the login.
