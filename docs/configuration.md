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
├── deployments\         packages made by Export for deployment: <project>_<date>\ with .zip, .bacpac and DEPLOY.txt
├── logs\                one file per day, kept 30 days: every operation's messages, one line each after its
│                        time (`18:21:16 Database seeded.`), [warning] / [error] marked, how it ended - the
│                        operation's title and stages are the Output tab's - and DNN Manager's own warnings and
│                        errors with their stack traces
├── packages\            downloaded DNN install packages, when projects.keepDnnPackages is on - per repository
│                        (<owner>.<repo>\), with releases.json: its versions at the last lookup, for offline use
├── projects\            how DNN Manager installed the projects it set up, one file each - data only, no state
└── state\               the workspace, for the next start (user guide: Picking up where you left off):
    ├── keep-warm.json   which sites are kept warm (and their own interval and pages) - switched on again at the
    │                    next start; saved the moment one is switched
    ├── window.json      where the window was, its size, the sidebar shown or hidden, the bottom panel's height
    ├── workspace.json   the page, the Projects table (search, filter, sorting, rows, scroll), the open Details and tab
    ├── forms.json       what was typed on New project, Host project and unsaved Settings - never a password
    ├── logs.json        the bottom panel's tab, its search, the site and log on the Logs tab
    └── update.json      only during an update: which one - the new version reads it once, then deletes it
```

The `state` files are saved a moment after something changes and when DNN Manager
closes, each on its own and whole (written beside the file, then moved over it), so
a crash leaves the last good one. Each has a `format` number: one of an older format
is read as the current one; one of a newer format (written by a newer DNN Manager) is
ignored and left as it is; one that can't be read is renamed `<name>.json.bad` and the
defaults are used - DNN Manager always starts. Deleting the folder (DNN Manager
closed) forgets the workspace and which sites are kept warm; nothing else depends on it.

`projects\` and `state\` are kept apart: `projects\` holds data about the projects
(how each was installed), `state\` what is switched on or remembered between starts.

An [update](user-guide.md#update) downloads into `%TEMP%\DnnManager-update\<version>\`,
with its log (`update.log`, and `update.setup.log` for Setup) - removed a couple of
minutes after the next start.

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
**Discard changes** puts the saved values back, and leaving the page with unsaved
changes asks first. Closing or restarting DNN Manager keeps them, unsaved, for the
next start - all but a changed password, which closing asks about. The values the page doesn't show, such
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
  "window": { "saveResourcesWhileMinimized": true },
  "keyboard": { "shortcuts": {} },
  "layout": { "sidebarPosition": "left", "panelAlignment": "center", "menuBarVisible": true, "statusBarVisible": true, "quickInputPosition": "top", "density": "default" }
}
```

| Key | Meaning |
|---|---|
| `version` | The format of the file. Don't change it - the app upgrades older files itself. |
| `projects.baseDirectory` | Where projects live (`C:\DNN` by default). |
| `projects.sitePort`, `projects.hostnameSuffix` | Sites answer at `http://<project>.<hostnameSuffix>[:sitePort]`. |
| `projects.dnnReleaseSources` | GitHub releases API URLs - the repositories **New project** offers, with their versions. |
| `projects.keepDnnPackages` | `false` by default. When `true`, each downloaded DNN install package is kept in `Documents\DnnManager\packages\<owner>.<repo>\` and used again when a new project picks the same version - no download. Each repository's version list is saved there too (`releases.json`), so New project works without internet for the kept versions. When `false`, the package is downloaded into the project and deleted after installing. |
| `projects.dnnDefaults.*` | What **New project** starts with for a new site: `installMode` (`automatic` or `manual`), `hostUsername` (`host`), `hostEmail` (`admin@admin.com`; empty: `host@<hostnameSuffix>`), `websiteName` (`My Website`; empty: the project's name), `language` (`en-US`, `de-DE`, `es-ES`, `fr-FR`, `it-IT` or `nl-NL`) and `template` (`Default Website` or `Blank Website`). The host password is not in the file: it is in the Windows Credential Manager of your account (`DnnManager/dnn-defaults/host-password`); while none is saved there it is `Admin@123`. Set in **Settings → Projects**. |
| `projects.keepWarm.*` | How the sites switched to [keep warm](user-guide.md#keep-warm) are kept warm: `pingMinutes` (`5`, 1 to 60) - the longest wait between two requests, shortened for a site whose app pool idles out sooner; `pingPath` (`/KeepAlive.aspx`) - the page requested to keep a running site warm (`/` keeps the home page's caches warm too, as Azure's *Always On* does); `warmUpPath` (`/`) - the page requested to warm up a site without a worker process. Pages are on the site itself (a leading `/` is added), never one of DNN's installer (`/Install/…`, `mode=`). Set in **Settings → Projects → Keep warm**. Which sites are kept warm is not in the file: it is in `state\keep-warm.json`. |
| `sqlServer.type` | Where a new project's database goes: `container` (the local SQL container - the default), `sqlServer` (SQL Server / SQL Server Express at `server`, with `authentication` `windows` or `sql` and, for `sql`, the login `userName` - its password is in the Windows Credential Manager, `DnnManager/database-server/password`) or `localDbFile` (the site's own `App_Data\Database.mdf` on the LocalDB instance `server`, e.g. `(LocalDB)\MSSQLLocalDB`). Set in **Settings → Database server**; a project keeps the database it was made with. |
| `sqlServer.host`, `port`, `userName`, `saPassword` | The local SQL container DNN Manager connects to: `host` (`localhost` by default), `port`, `userName` (the login it signs in with - `sa` when empty; another one has to exist on the container already - the same setting as the SQL Server login of `sqlServer.type` `sqlServer`) and `saPassword` (that login's password - and the `sa` password **Set up docker-compose** creates the container with). The password is stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - not hashed, since DNN Manager needs it to sign in. To change it in the file, replace the value with the new password as plain text; it's encrypted on the next start. A new project's database is named like the project. The Docker container publishes SQL Server on this port with this password - on this PC only (`127.0.0.1`) when `host` is `localhost` or `127.0.0.1`, on every network interface for any other host (this PC's address on the network, for a VM). An existing data volume keeps the sa password it was created with. |
| `docker.*` | The SQL Server container: `containerName`, `volumeName`, `edition` (`MSSQL_PID`) and `collation`. **Settings → Docker container → Set up docker-compose** makes the container from these (and `sqlServer.port` / `saPassword`); **Show docker-compose.yml** shows the file to copy. |
| `ssms.rememberPassword` | `false` by default. When `true`, signing SSMS in from the project menu ticks its *Remember Password*, so SSMS keeps the password. On the Settings page under **Database server**. |
| `iis.requiredFeatures` | IIS Windows features checked (and optionally enabled). |
| `appearance.theme` | `system` (follow the Windows app theme), `light` or `dark`. Set from the gear's **Themes** (or the command palette's *Color theme…*), applied and saved at once. |
| `appearance.uiScale` | `100` by default: everything in the window - text, icons and spacing, menus and tooltips too - at this percentage (50 to 200; Settings offers 80-175). Set in **Settings → General**, applied at once. |
| `appearance.fontSize` | `13` by default: the app's text size in pixels (8 to 32; Settings offers 11-18) - titles and hints keep their proportions, icons keep their size. The terminal has its own (`terminal.fontSize`). Set in **Settings → General**, applied at once. |
| `appearance.projectColumns` | The optional columns the Projects table shows: `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`, `status`, `url`, `ports`, `id`, `size`, `memoryPercent`, `disk`, `network`, `path`. Set by the table's **Columns** button; the default is `url`, `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`. |
| `terminal.*` | The terminal panel, set in **Settings → General** and applied at once: `enabled` (`false`: no Terminal tab, no shells), `defaultShell` (`powershell`, `pwsh`, `cmd` or `gitbash` - the first installed one when that one isn't), `fontFamily` (empty for Cascadia Mono, or Consolas) and `fontSize` (8 to 32) - also the font of the Output and Logs tabs. |
| `keyboard.shortcuts` | The keyboard shortcuts changed from their defaults, by command: `{ "project.start": "Ctrl+F5", "view.close": "" }` - an empty one takes the command's shortcut away; a command not listed has its default. Written as VS Code writes them (`Ctrl+Shift+P`, `Ctrl+,`, ``Ctrl+` ``, `Shift+F5`); a shortcut needs Ctrl or Alt, or a function key. Set in **Settings → Keyboard shortcuts**, applied and saved at once - see [Keyboard](user-guide.md#keyboard). |
| `layout.*` | How the window is laid out, as VS Code's **Customize Layout** sets it - applied and saved at once (see [Layout](user-guide.md#layout)): `sidebarPosition` (`left` or `right`), `panelAlignment` (`center` - under the page only; `justify` - the window's width; `left` / `right` - to that edge of the window, under the sidebar when it is on that side), `menuBarVisible` (`true` - for now the app's name in the title bar), `statusBarVisible` (`true`), `quickInputPosition` (where the command palette opens: `top` or `center`) and `density` (`default` - the sidebar, page and panel as rounded cards with a gap between them; or `compact` - flush, divided by lines, with a narrower sidebar (190 pixels, 40 with icons only), tighter entries and narrower title bar buttons). Whether the sidebar and the panel are shown is not here: it is the workspace's (`state\window.json`). |
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
