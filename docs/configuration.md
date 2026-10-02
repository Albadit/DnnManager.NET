# Configuration

Where DNN Manager keeps its files and settings, every key of `settings.json`,
what happens when the file is wrong, and the environment variables that override
it. Most settings are set on the **Settings** page ([user guide](user-guide.md));
this is the reference.

## Where your files are

DNN Manager keeps your files apart from the program, in your **Documents**
folder, so updating, reinstalling or uninstalling the app never touches them:

```text
Documents\DnnManager\
├── settings.json        your settings
├── backups\             project backups (user guide: Backups) and settings.json copies made
│                        before an upgrade of its format or a reset
├── logs\                one file per day, kept 30 days: every operation (Markdown - # an operation, ## its stages,
│                        a line per message with its time, [warning] / [error] marked, how it ended) and
│                        DNN Manager's own warnings and errors with their stack traces
├── packages\            downloaded DNN install packages, when projects.keepDnnPackages is on
└── projects\            how DNN Manager installed the projects it set up, one file each
    └── keep-warm\       the sites kept warm, one file each
```

The folder and `settings.json` are created the first time the app starts. When
you upgrade from 1.1 or earlier, the `appsettings.json`
next to the old DNN Manager 1.1 exe is carried over when the new
version is started from that same folder. After installing somewhere else, copy
`appsettings.json` into `Documents\DnnManager` as `settings.json` and it is
converted on the next start.

## Changing settings

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

## settings.json

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
| `projects.keepWarm.*` | How the sites switched to [keep warm](user-guide.md#keep-warm) are kept warm: `pingMinutes` (`5`, 1 to 60) - the longest wait between two requests, shortened for a site whose app pool idles out sooner; `pingPath` (`/KeepAlive.aspx`) - the page requested to keep a running site warm (`/` keeps the home page's caches warm too, as Azure's *Always On* does); `warmUpPath` (`/`) - the page requested to warm up a site without a worker process. Pages are on the site itself (a leading `/` is added), never one of DNN's installer (`/Install/…`, `mode=`). Set in **Settings → Projects → Keep warm**. Which sites are kept warm is not in the file: it is in `projects\keep-warm\`. |
| `sqlServer.type` | Where a new project's database goes: `container` (the local SQL container - the default), `sqlServer` (SQL Server / SQL Server Express at `server`, with `authentication` `windows` or `sql` and, for `sql`, the login `userName` - its password is in the Windows Credential Manager, `DnnManager/database-server/password`) or `localDbFile` (the site's own `App_Data\Database.mdf` on the LocalDB instance `server`, e.g. `(LocalDB)\MSSQLLocalDB`). Set in **Settings → Database server**; a project keeps the database it was made with. |
| `sqlServer.host`, `port`, `userName`, `saPassword` | The local SQL container DNN Manager connects to: `host` (`localhost` by default), `port`, `userName` (the login it signs in with - `sa` when empty; another one has to exist on the container already - the same setting as the SQL Server login of `sqlServer.type` `sqlServer`) and `saPassword` (that login's password - and the `sa` password **Set up docker-compose** creates the container with). The password is stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - not hashed, since DNN Manager needs it to sign in. To change it in the file, replace the value with the new password as plain text; it's encrypted on the next start. A new project's database is named like the project. The Docker container publishes SQL Server on this port with this password - on this PC only (`127.0.0.1`) when `host` is `localhost` or `127.0.0.1`, on every network interface for any other host (this PC's address on the network, for a VM). An existing data volume keeps the sa password it was created with. |
| `docker.*` | The SQL Server container: `containerName`, `volumeName`, `edition` (`MSSQL_PID`) and `collation`. **Settings → Docker container → Set up docker-compose** makes the container from these (and `sqlServer.port` / `saPassword`); **Show docker-compose.yml** shows the file to copy. |
| `ssms.rememberPassword` | `false` by default. When `true`, signing SSMS in from the project menu ticks its *Remember Password*, so SSMS keeps the password. On the Settings page under **Database server**. |
| `iis.requiredFeatures` | IIS Windows features checked (and optionally enabled). |
| `appearance.theme` | `system` (follow the Windows app theme), `light` or `dark`. Set in **Settings → General**. |
| `appearance.uiScale` | `100` by default: everything in the window - text, icons and spacing, menus and tooltips too - at this percentage (50 to 200; Settings offers 80-175). Set in **Settings → General**, applied at once. |
| `appearance.fontSize` | `13` by default: the app's text size in pixels (8 to 32; Settings offers 11-18) - titles and hints keep their proportions, icons keep their size. The terminal has its own (`terminal.fontSize`). Set in **Settings → General**, applied at once. |
| `appearance.projectColumns` | The optional columns the Projects table shows: `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`, `status`, `url`, `ports`, `id`, `size`, `memoryPercent`, `disk`, `network`, `path`. Set by the table's **Columns** button; the default is `url`, `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`. |
| `terminal.*` | The terminal panel, set in **Settings → General** and applied at once: `enabled` (`false`: no Terminal tab, no shells), `defaultShell` (`powershell`, `pwsh`, `cmd` or `gitbash` - the first installed one when that one isn't), `fontFamily` (empty for Cascadia Mono, or Consolas) and `fontSize` (8 to 32) - also the font of the Output and Logs tabs. |
| `window.saveResourcesWhileMinimized` | `true` by default: while the window is minimized, what only it shows pauses and, while nothing runs, Windows runs DNN Manager power-efficiently - see [Efficiency mode](user-guide.md#efficiency-mode-while-the-window-cant-be-seen). `false`: everything goes on as while the window is shown. Set in **Settings → General**, applied at once. |

## When the file is wrong

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

## Environment variables

Environment variables prefixed with `DNNMANAGER_` override settings, e.g.
`DNNMANAGER_DnnManager__Docker__SaPassword=...` (names as in `AppOptions`). The
Settings page lists any that are set, since they win over what it saves.

> The folder is the Documents folder of the Windows account the app runs as. If
> you sign in to the UAC prompt with a *different* administrator account, that
> account's Documents is used.
