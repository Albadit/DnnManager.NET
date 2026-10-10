# Troubleshooting

Problems you may run into, what usually causes them and how to fix them. Two
places tell you most of what went wrong:

- **The Output tab** (bottom panel) - every step of every operation, with the
  error and what to do next.
- **The log file** `Documents\DnnManager\logs\dnnmanager-<yyyymmdd>.log` - the same
  messages, one line each after its time (the title and stages are the Output
  tab's), plus DNN Manager's own warnings and errors with their stack traces
  (`[warning]`, `[error]`, `[critical]`). See
  [configuration.md](configuration.md#where-your-files-are).

## DNN Manager

### It says it needs Administrator rights and closes

- **Cause:** it manages IIS, so it always runs elevated. The UAC prompt was
  declined, or the account can't elevate.
- **Fix:** start it again and accept the prompt, or sign in with an account
  that can run programs as Administrator. **Settings → General → Start DNN
  Manager when you sign in** starts it elevated without a prompt at sign-in -
  only for DNN Manager installed for all users, in Program Files (Setup's
  default; a per-user installation is offered the move by a new Setup).

### "DNN Manager can't use its settings" at start

- **Cause:** `Documents\DnnManager\dnnmanager.db` can't be opened or read
  (another program has it open - a SQLite browser, say -, or Windows Security's
  *Controlled folder access* blocks DNN Manager), the settings were saved by a
  newer DNN Manager, or the `sa` password was encrypted by another Windows
  account or on another PC. A value that can't be read or isn't allowed doesn't
  stop the start any more - it goes back to its default (below). A **damaged**
  file isn't among them either: it is put aside at the start and DNN Manager
  goes on from its last copy or the defaults (see
  [configuration.md](configuration.md#where-your-files-are)).
- **Fix:** the dialog names the problem. Fix what it names (close the other
  program, allow `DnnManager.exe` through Controlled folder access, update DNN
  Manager) and press **Try again** - or **Reset to defaults**, which starts
  with the defaults (the current settings aren't kept). **Exit** changes
  nothing. See
  [configuration.md](configuration.md#when-the-settings-cant-be-used).

### Warnings at the start

Something found while DNN Manager starts shows as a toast - several are
counted in one - with **Show output**, and stays on the Output tab and in the
log file. What each means and what to do:

- **"A saved setting isn't allowed by this version of DNN Manager and is back
  at the default"** - the value (named, with why) was changed by hand, by
  another program, or is refused by a rule this version added (a projects
  folder in `AppData\Local\Programs`, say). The other settings are as you saved
  them. Check the named ones in **Settings** and save a value that is allowed
  ([configuration.md](configuration.md#the-settings)).
- **`DNNMANAGER_*` variables ignored** - one of them breaks a rule (named, with
  its settings key), so none is used. Fix or remove it in Windows' environment
  variables and start DNN Manager again
  ([configuration.md](configuration.md#environment-variables)).
- **"DNN Manager's data folder … is in OneDrive"** (or **on a network share**)
  - your Documents is backed up to OneDrive (or redirected), so your backups,
  deployment packages, logs and settings database are copied there. To keep
  them on this PC, stop backing up Documents (OneDrive → Settings → Sync and
  back up → Manage back up), or delete what you don't need in **Troubleshoot →
  Clean up data**; **Settings → Projects → Backups** can delete old backups by
  themselves.
- **"Windows' Developer Mode is on"** - programs without administrator rights
  can make symbolic links, which one could put where DNN Manager deletes or
  writes. DNN Manager doesn't follow them, but unless you need it, turn
  Developer Mode off (Settings → System → For developers).
- **"This Windows doesn't have the protection that keeps DNN Manager from
  following junctions other programs made (redirection trust)"** - run Windows
  Update and install the latest updates, then restart DNN Manager.
- **"DNN Manager started with environment variables that make .NET load other
  code"** - the variables are named (`CORECLR_PROFILER`,
  `DOTNET_DiagnosticPorts`, …). Unless you set them yourself, remove them
  (System → About → Advanced system settings → Environment Variables) and check
  the PC for malware. An installed DNN Manager is started through its launcher,
  which leaves them out; the portable exe isn't.
- **"'…' didn't finish - DNN Manager ended while it ran"** - DNN Manager
  crashed, Windows shut down, or you chose **Quit now** while an operation ran.
  The message lists what it would have undone: look at those (a folder, an IIS
  site, a database) and remove what is half made - **Remove…** for a project.
- **"Start DNN Manager when you sign in" is switched off** - its task would
  start DNN Manager from a folder programs without administrator rights can
  change (a per-user installation). Let a new Setup move DNN Manager to Program
  Files, then turn it on again.

### My settings are gone

- **Cause:** DNN Manager was updated from 1.7.1 or earlier. This version keeps
  its data in `dnnmanager.db` and doesn't read the earlier `settings.json`,
  `state\` and `projects\`.
- **Fix:** set the settings again in the app and switch keep warm on again for
  your sites. The old files are still there to look at, and can be deleted. See
  [configuration.md](configuration.md#upgrading-from-171-or-earlier).
- **Cause:** the settings are in the **Documents** folder of the Windows account
  DNN Manager runs as. Signing in to the UAC prompt with a *different*
  administrator account uses that account's Documents.
- **Fix:** accept the UAC prompt with your own account, or copy
  `Documents\DnnManager` to the other account's Documents.

### "Reconnecting…" top right on the Projects page

- **Cause:** IIS's configuration or the projects folder can't be read - IIS
  is being reconfigured, or the folder is gone or inaccessible.
- **Investigate:** rest the mouse on it for the reason and the time of the last
  synchronisation; the log file has the details.
- **Fix:** it tries again every 5 seconds by itself. If it stays, check that IIS
  is installed (**Settings → IIS → Test**) and that the projects folder
  (**Settings → Projects**) exists.

### DNN Manager closed by itself

- **Investigate:** the last `[critical]` line in the day's log file has the
  exception and its stack trace.
- **Fix:** report it with that part of the log.

### The update failed, or About says "Unable to reach GitHub"

- **Unable to reach GitHub** (gray dot): no internet, a proxy or firewall in the
  way, or GitHub's rate limit (60 requests an hour per IP address). The reason is
  under the status; nothing is wrong with DNN Manager. It asks again at the next
  start, when About is opened, and with **Check for updates** in the command palette.
- **The download or its check failed** (*Update failed* on About, and in the Update
  button's tooltip): nothing was changed - DNN Manager kept running. Click **Update**
  to try again; a download that isn't the file
  GitHub published (another size, SHA-256 or version) is never used, and neither is
  a release GitHub lists no SHA-256 for. A download that stops getting anything for
  a minute counts as failed.
- **It closed and the old version came back** with *The update to vX wasn't
  installed*: **Show log** opens `Documents\DnnManager\logs\update-failed-<version>.log`
  - the update helper's log and Setup's, copied there from
  `%ProgramData%\DnnManager\temp\update\<version>\` (which is cleaned up); the
  newest two such files are kept. For a portable exe in a folder you can't
  write to, move it somewhere you can. You can always install the release by hand
  from GitHub - your settings are in `Documents\DnnManager` either way.

### It opens somewhere odd, or keeps bringing back something I don't want

- **Cause:** DNN Manager opens the way it was left - the page, the Details, the
  Logs tab's log, unsaved form values ([Picking up where you left
  off](user-guide.md#picking-up-where-you-left-off)).
- **Fix:** close what you don't want and it is gone next time (the Details,
  **Discard changes** on Settings). To start completely fresh: **Troubleshoot →
  Reset to factory defaults** forgets the workspace - and resets the settings
  and more too (see below). A value of the workspace that can't be read keeps
  its default by itself (the log file names it); the rest is used as saved.

### Looking at DNN Manager's data, or starting from the defaults

DNN Manager's own data - settings, workspace, which sites are kept warm, how
each project was installed, the saved DNN versions - is one SQLite database,
`Documents\DnnManager\dnnmanager.db` ([configuration.md](configuration.md#where-your-files-are)).

- **To look at it:** close DNN Manager and open the file with any SQLite
  browser (DB Browser for SQLite, for one) - the settings are the rows of
  `settings`, one per value ([configuration.md](configuration.md#looking-at-the-database)
  has examples). Change nothing there - everything in it is changed in the
  app - and close the browser before starting DNN Manager again.
- **To start from the defaults:** close DNN Manager and delete
  `dnnmanager.db`. The next start makes it again, with the defaults: the
  settings, the workspace, which sites are kept warm, the project records (how
  each project was installed) and the saved DNN versions are lost. Your
  projects themselves - their IIS sites, folders and databases - stay, and so do
  the backups, logs, kept packages and the passwords in the Windows Credential
  Manager. Keep a copy of the file first if you may want it back.
  **Troubleshoot → Reset to factory defaults** does much the same from within
  the app - it also removes the saved passwords, logs and kept packages, and
  keeps the project records.

## Sites

### A site doesn't open without internet, or at its custom domain

The browser says it can't find the server (`ERR_NAME_NOT_RESOLVED`), though the
site runs in IIS.

- **Cause:** the host name isn't in the hosts file, so it is asked of DNS -
  which for `*.dnndev.me` only answers while the PC is online, and for a custom
  domain doesn't point to this PC at all. DNN Manager writes the line when it
  starts and when a site's host names change
  ([Host names and working offline](user-guide.md#host-names-and-working-offline)).
- **Investigate:** a warning *The hosts file … can't be written* (a toast, and
  the log file); the hosts file - **Logs** tab → **DNN Manager** → **Hosts
  file** - and its block between `# DNN Manager: local sites - begin` and
  `- end`; in PowerShell,
  `Resolve-DnsName <host name>` should answer `127.0.0.1` from the hosts file.
- **Fix:** the file read-only - clear *Read-only* in its properties; security
  software blocking it - allow DNN Manager to change the hosts file. Then start
  DNN Manager again, or change any site's host names. A wildcard binding
  (`*.shop.test`) can't be in the hosts file - bind the site to each name.

### SQL shows **Offline** for a site

- **Cause:** the database server in the site's `web.config` doesn't answer, or
  refuses the login. The tooltip says what was asked and what it answered
  (e.g. *Login failed*).
- **Investigate:** for the local SQL container, **Settings → Docker container →
  Test** - is Docker Desktop running, does the container exist and run? For
  another server: is it running, reachable, and does the login in `web.config`
  work there?
- **Fix:**
  - Docker Desktop not running: **Start Docker Desktop** on the same card.
  - No container: **Set up docker-compose**.
  - *Login failed* for `sa` on the container: the container keeps the `sa`
    password its data volume was created with - changing **SA password** in
    Settings doesn't change it. Put the password the volume was created with
    back in **Settings → Database server**.
- **Verify:** the SQL column turns **Live** within 10 seconds.

### No SQL state for a site on another server

- **Cause:** the site signs in with Windows authentication to a SQL Server
  that isn't on this PC and isn't the one in **Settings → Database server**.
  The site's `web.config` names that server, and its app pool can change it,
  so DNN Manager doesn't send your Windows sign-in there every 10 seconds
  ([security.md](security.md#administrator-rights)).
- **Fix:** choose that server in **Settings → Database server**, or let the
  site sign in with SQL authentication. **Open in SSMS**, **Upgrade DNN**,
  **Restore backup** and portal-alias edits ask before they sign in there -
  *Don't sign in* stops them with nothing changed.

### Database shows *not set*

- **Cause:** the site's `web.config` is missing or unreadable, has no
  `SiteSqlServer` connection, or still has DNN's own (a site DNN hasn't been
  installed into yet). The tooltip says which.
- **Fix:** install DNN (its wizard writes the connection), or set the connection
  with **Host project** ([user guide](user-guide.md#host-a-project)).

### The first page after a while takes 10-30 seconds

- **Cause:** IIS stops a site's worker process after its idle time-out (20
  minutes by default); the next request starts DNN again.
- **Fix:** switch on **Keep warm** for the site (the flame in its row) - see
  [Keep warm](user-guide.md#keep-warm). It only works while DNN Manager runs.

### Keep warm shows a red dot, or stays grey

- **Cause:** red - requests fail (the site errors, or its database doesn't
  answer); grey - it is paused on purpose: the site or IIS is stopped, an
  operation runs on it, or a debugger is attached to its worker process.
- **Investigate:** the site's **Keep warm** card on its Details page.
- **Fix:** fix what the site's own error says; then **Check now**. A failing
  site is tried again after 1, 2 and 5 minutes, then left alone until you press
  it.

### The site shows DNN's "Connection To The Database Failed"

- **Cause:** DNN shows this page for *any* error while it starts, not only a
  database one.
- **Investigate:** the real error is in the site's
  `Portals\_default\Logs\<date>.log.resources`. The **Logs** tab (site's
  right-click → **View logs**) shows it.

### Automatic DNN setup failed

- **Cause:** shown in the Output tab, with DNN's own messages.
- **Fix:** when DNN's own installation failed, the project is left as it is so you
  can look into it; then remove it (**Remove…**) and create it again - DNN can't
  install twice into the same files and database. When the setup failed before
  that (the download, the IIS site, the database), what it had made is taken away
  again: correct the cause and create it again under the same name.

### "The database '…\APP_DATA\DATABASE.MDF' cannot be opened because it is version 998"

- **Cause:** a site with a LocalDB file, on a PC with two LocalDB versions -
  often Visual Studio's 2019 next to 2025. LocalDB is one instance per Windows
  account, at the version it was made with: the site's (its app pool identity's,
  new) is 2025 and makes the file a 2025 one (version 998), the user's own
  `MSSQLLocalDB`, made earlier by 2019, only opens up to 904. DNN Manager 1.7.7
  and older opened the file in the user's instance.
- **Fix:** newer versions open the file in a LocalDB of the file's own version:
  the user's `MSSQLLocalDB` when it is that version, else an instance of DNN
  Manager's own (`DnnManager17` for 2025), made the first time. Remove the
  project that failed and create it again. With an older DNN Manager, make the
  user's instance the newest version (`sqllocaldb stop MSSQLLocalDB`,
  `sqllocaldb delete MSSQLLocalDB`, `sqllocaldb create MSSQLLocalDB 17.0 -s`) -
  databases attached to the old one are detached, their files stay.

### Windows blocked a DNN file ("blocked by an application control policy")

- **Cause:** Smart App Control (or a company's App Control policy) stopped IIS
  from loading one of DNN's DLLs: most of them aren't signed, and a rarely seen
  one - `DNN.Connectors.GoogleAnalytics4.dll` in DNN 10.3.3 - has no reputation
  yet. DNN's installer then answers *HTTP 500* with that message (Windows error
  `0x800711C7`).
- **Fix:** see whether it is Smart App Control - **Windows Security → App &
  browser control → Smart App Control settings**. Turned off, the site works;
  on most Windows 11 versions it can't be turned on again without reinstalling
  Windows. A policy of your organisation is for its IT to allow. Windows' log
  **Applications and Services Logs → Microsoft → Windows → CodeIntegrity →
  Operational** names each blocked file (event 3077).

### Remove… couldn't delete the folder

- **Cause:** files in the folder are in use - an editor, a terminal whose
  working folder is inside, an Explorer window.
- **Fix:** DNN Manager lists the programs and closes them after you confirm.
  Windows itself, services and Explorer are never closed: what they still hold
  is deleted at the next Windows restart.

### "Didn't start …" or "DNN Manager doesn't start … as Administrator: programs without administrator rights could change it"

DNN Manager runs as Administrator, and runs a program with those rights only
when nobody but administrators can change it - a copy in your own folders, or on
your own PATH, could be swapped by any program you run
([security.md](security.md#administrator-rights)). The message names the copy it
found. Install that program **for all users** (into Program Files):

- **docker**: Docker Desktop installs for all users by default.
- **dotnet** (to install SqlPackage): the .NET SDK installer, not a user-only
  `dotnet-install` script.
- **winget**: comes with Windows' App Installer; if it is refused, install Docker
  Desktop from its website instead.
- **SQL Server Management Studio** greyed in **Open with…**, with this in its
  tooltip: install SSMS in its default folder, in Program Files.
- **A shell greyed under New Administrator terminal**: PowerShell 7 and Git for
  Windows installed *for all users* are offered there; per-user installs are
  only offered for ordinary terminals, which run as you.
- **"Didn't run docker compose: Docker would use …docker-compose.exe"** -
  Docker found a compose plugin in a folder your account can change (usually
  `%ProgramData%\Docker\cli-plugins`, which any user may make when Docker
  didn't). Remove that copy - Docker Desktop has its
  own, in Program Files - and **Set up docker-compose** again.

Editors (VS Code's user installer, Rider…) are not affected: DNN Manager opens
them as you, without its rights. DNN Manager looks programs up on the
computer's PATH only - a folder you added to your own PATH isn't used.

### A terminal "couldn't be started" without administrator rights

The terminals run as you: DNN Manager makes a copy of its own sign-in without
the administrator rights. When Windows refuses that (the message says which
step failed), no terminal is started - and none with administrator rights in
its place. Restart DNN Manager and try again. For a terminal with those rights,
choose **New Administrator terminal** (the arrow beside **+**). A drive letter
missing in a terminal is one mapped in your everyday session: DNN Manager's
runs separately - map it again there (`net use`).

### "untrusted mount point", or "is reached through a link or junction"

A folder DNN Manager deletes or writes in - a site's `bin`, its
`Portals\_default\Cache`, a folder in `Documents\DnnManager` - is a junction (or
a symbolic link) that was made without administrator rights. With its
Administrator rights DNN Manager would delete or write wherever the junction
points, so it doesn't follow it
([security.md](security.md#administrator-rights)). A common case is a module's
source folder linked into `DesktopModules` while developing: IIS still serves
it, but DNN Manager's copy, backup or restore stops there. The message ends
with a `Hint:` line - the Output tab shows it under the error as what to do.
Make the junction again from an administrator prompt (`mklink /J <link>
<target>`), or copy the files in instead. **Clone** doesn't stop: it leaves
the linked folders out and names them in a warning - copy what they point to
into the clone, or make the links again there. **Clear website cache** refuses
a site whose folder itself is a link.

### "The kept package … isn't the file GitHub lists for the release any more"

A DNN package kept in `Documents\DnnManager\packages` no longer has the SHA-256
GitHub listed for it (saved with the version list, so this is checked offline
too) - something changed the file. It has been deleted: try again, and it is
downloaded anew (that needs internet).

### A package or backup is refused: not enough disk space, or an unsafe path

- **Disk space**: DNN Manager refuses to unpack a zip that would leave less
  than 512 MB free on the drive - before anything is written. Free space on
  the projects folder's drive (**Troubleshoot → Clean up data**, old backups)
  and try again.
- **An unsafe path**: an entry that would land outside the site, on another
  file's stream (a name with `:`), or through a link refuses the whole package
  or backup - nothing of it is written. It wasn't made by DNN or DNN Manager;
  use another copy.

### A remote SQL Server: "The certificate chain was issued by an authority that is not trusted"

DNN Manager checks the certificate of a SQL Server on another computer (a
clone's live source, Azure SQL): without that check anyone on the way could
read the login and change the data. Connect by the name the certificate is
made out to - for Azure SQL, `<server>.database.windows.net` - or, for a
server of your own with a self-signed certificate, import that certificate into
this PC's **Trusted Root Certification Authorities** (`certlm.msc`). Servers on
this PC (the container, LocalDB) are not affected.

### The hosts file got damaged

DNN Manager copies the hosts file to `hosts.dnnmanager.bak`, beside it in
`C:\Windows\System32\drivers\etc`, before each change. Should the file be cut off
(a crash or a power cut while it was written), copy that file over `hosts` - from
an elevated prompt: `copy /y %SystemRoot%\System32\drivers\etc\hosts.dnnmanager.bak %SystemRoot%\System32\drivers\etc\hosts`.

### IIS features missing, or a site gives HTTP errors right after setup

- **Fix:** **Settings → IIS → Test** lists the Windows features DNN needs;
  **Set up IIS** enables the missing ones. Windows may ask for a restart.

## Building

### "Access to the path '…\publish\DnnManager.exe' is denied" when publishing

The app is still running from `publish\` - close it and publish again
([releasing.md](releasing.md)).

### "Publishing the launcher failed" when building the installer

The launcher is compiled with Native AOT, which links with Visual Studio's C++
tools. Install *Desktop development with C++* (or the Build Tools for Visual
Studio with it), or build without the launcher: `build.ps1 -NoLauncher` - never
for a release ([Build the installer](releasing.md#build-the-installer)).
