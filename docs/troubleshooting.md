# Troubleshooting

Problems you may run into, what usually causes them and how to fix them. Two
places tell you most of what went wrong:

- **The Output tab** (bottom panel) - every step of every operation, with the
  error and what to do next.
- **The log file** `Documents\DnnManager\logs\dnnmanager-<date>.log` - the same
  steps, plus DNN Manager's own warnings and errors with their stack traces
  (`[warning]`, `[error]`, `[critical]`). See
  [configuration.md](configuration.md#where-your-files-are).

## DNN Manager

### It says it needs Administrator rights and closes

- **Cause:** it manages IIS, so it always runs elevated. The UAC prompt was
  declined, or the account can't elevate.
- **Fix:** start it again and accept the prompt, or sign in with an account
  that can run programs as Administrator. **Settings → General → Start DNN
  Manager when you sign in** starts it elevated without a prompt at sign-in.

### A dialog about settings.json at start

- **Cause:** the file isn't valid JSON, has a value of the wrong type or one
  that isn't allowed (a port above 65535, a drive as the projects folder…), or
  comes from a newer DNN Manager.
- **Fix:** the dialog names the problem: fix the file and press **Try again**,
  or **Reset to defaults** (the old file is kept in `backups\`). See
  [configuration.md](configuration.md#when-the-file-is-wrong).

### My settings are gone

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

## Sites

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
- **Investigate:** the site's **Keep warm** card on its Details page, and the
  **Background** list in the Output tab.
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
- **Fix:** the project is left as it is so you can look into it; then remove it
  (**Remove…**) and create it again - DNN can't install twice into the same
  files and database.

### Remove… couldn't delete the folder

- **Cause:** files in the folder are in use - an editor, a terminal whose
  working folder is inside, an Explorer window.
- **Fix:** DNN Manager lists the programs and closes them after you confirm.
  Windows itself, services and Explorer are never closed: what they still hold
  is deleted at the next Windows restart.

### IIS features missing, or a site gives HTTP errors right after setup

- **Fix:** **Settings → IIS → Test** lists the Windows features DNN needs;
  **Set up IIS** enables the missing ones. Windows may ask for a restart.

## Building

### "Access to the path '…\publish\DnnManager.exe' is denied" when publishing

The app is still running from `publish\` - close it and publish again
([releasing.md](releasing.md)).
