# Configuration

Where DNN Manager keeps its data and settings, every settings key, what happens
when the settings can't be used, and the environment variables that override
them. The settings are set in the app - the **Settings** page and the other
places named below ([user guide](user-guide.md)); this is the reference.

## Where your files are

DNN Manager keeps your data apart from the program, in your **Documents**
folder, so updating, reinstalling or uninstalling the app never touches it:

```text
Documents\DnnManager\
├── dnnmanager.db        DNN Manager's own data, one SQLite database (below)
├── backups\             project backups (user guide: Backups)
├── deployments\         packages made by Export for deployment: <project>_<date>\ with .zip, .bacpac and DEPLOY.txt
├── logs\                one file per day, kept 30 days: every operation's messages, one line each after its
│                        time (`18:21:16 Database seeded.`), [warning] / [error] marked, how it ended - the
│                        operation's title and stages are the Output tab's - and DNN Manager's own warnings and
│                        errors with their stack traces
└── packages\            downloaded DNN install packages, when projects.keepDnnPackages is on - per repository
                         (<owner>.<repo>\)
```

The folders are made at the first start, empty until something goes in them.

`dnnmanager.db` holds five tables. Every value is in a column or a row of its
own - there is no JSON in it:

| Table | Holds |
|---|---|
| `settings` | The settings, one row per value: `key` (the value's path, e.g. `projects.sitePort`) and `value` (as text, e.g. `80`) - see [How the settings are saved](#how-the-settings-are-saved) |
| `state` | The workspace kept for the next start ([user guide](user-guide.md#picking-up-where-you-left-off)), one row per value - `area`, `key`, `value` - in seven areas: `window` (where the window was, its size, the sidebar shown or hidden, the bottom panel's height), `workspace` (the page, the Projects table - search, filter, sorting, rows, scroll - the open Details and tab), `forms` (what was typed on New project, Host project and unsaved Settings - never a password), `logs` (the bottom panel's tab, its search, the site and log on the Logs tab) `update` (only during an update: which one - the new version reads it once, then deletes it), `version` (the version that ran last - a newer one shows What's new at its first start) and `palette` (the commands last run from the command palette, listed first as *recently used*) |
| `projects` | How DNN Manager installed each project it set up - `site`, `install_mode`, `created_utc`, `dnn_version`, `host_user_name` - shown on the overview's **DNN** tab. Data about the project only, no secrets |
| `keep_warm` | The sites kept warm - `site`, a row each. How they are kept warm (interval, pages) is the same for every site: `projects.keepWarm.*`. Saved the moment one is switched; the same sites are kept warm at the next start |
| `dnn_releases` | Each repository's DNN versions as GitHub last listed them - asked at every start, and offered by New project when GitHub can't be reached |

The workspace is saved a moment after something changes and when DNN Manager
closes, each area on its own and whole - the database writes it completely or
not at all, so a crash leaves the last good one. A value that can't be read
keeps its default (the log file names it) and the rest of the area is read -
DNN Manager always starts. **Troubleshoot → Reset to factory defaults** resets
the settings and removes the `state` and `keep_warm` rows;
**Clean up data → kept DNN packages** empties `dnn_releases` with the packages (filled again at the next start).

The tables are made at the first start. A change of them in a later version is
made once, at that version's first start (the file's `PRAGMA user_version`
says how far it is).

An [update](user-guide.md#update) downloads into `%TEMP%\DnnManager-update\<version>\`,
with its log (`update.log`, and `update.setup.log` for Setup) - removed a couple of
minutes after the next start.

### Looking at the database

Close DNN Manager, then open `dnnmanager.db` with any SQLite browser (DB
Browser for SQLite, for one) - only to look: everything in it is changed in the
app. For example:

```sql
SELECT key, value FROM settings WHERE key LIKE 'projects.%' ORDER BY key;
SELECT area, key, value FROM state WHERE area = 'window';
SELECT * FROM keep_warm;
```

Close the browser before starting DNN Manager again, so the two don't use the
file at the same time.
Deleting `dnnmanager.db` (DNN Manager closed) starts from the defaults - see
[troubleshooting.md](troubleshooting.md#looking-at-dnn-managers-data-or-starting-from-the-defaults).

### Upgrading from 1.7.1 or earlier

Versions up to 1.7.1 kept their data in files in `Documents\DnnManager` -
`settings.json`, `state\` and `projects\`. This version doesn't read them: it
starts with the default settings, opens as on a first start, keeps no site warm
and has no record of how the existing projects were installed. Set your
settings again in the app (Settings - the SQL container's `sa` password too -,
Customize Layout, Keyboard shortcuts, the Projects table's columns) and switch
keep warm on again for your sites. The old files stay where they are,
untouched - delete them once you no longer need them. Your projects, backups,
logs and kept packages, and the passwords in the Windows Credential Manager,
are kept.

## Changing settings

Edit the settings on the **Settings** page, then press **Save** at the bottom
of the page. The values are checked first (full path, valid ports and URLs,
required fields); a problem shows as a warning and nothing is saved. Saved
settings apply at once, without a restart: another projects folder shows its
projects in the table, a new site address or SQL Server is what the next
operation uses. (Not while an operation runs - save once it has finished.)
**Discard changes** puts the saved values back, and leaving the page with unsaved
changes asks first. Closing or restarting DNN Manager keeps them, unsaved, for the
next start - all but a changed password, which closing asks about.

A few settings are changed where they are used, applied and saved at once: the
theme (the gear's **Themes**), the window's layout (**Customize Layout**), the
keyboard shortcuts (**Settings → Keyboard shortcuts**) and the Projects table's
columns (its **Columns** button).

The settings are not a file to edit: they are rows in `dnnmanager.db`, and
everything is changed in the app.

## The settings

Each setting is one row of the `settings` table in `dnnmanager.db`, its key the
path in the table below - `projects.sitePort` = `80` (see [How the settings are
saved](#how-the-settings-are-saved)). A key ending in `.*` stands for the keys
under it. Each key is set on the **Settings** page unless the table names
another place.

| Key | Meaning |
|---|---|
| `version` | The layout of the settings (`3`), written by the app. Settings with a higher one were saved by a newer DNN Manager and aren't used - see [When the settings can't be used](#when-the-settings-cant-be-used). |
| `projects.baseDirectory` | Where projects live (`C:\DNN` by default). |
| `projects.sitePort`, `projects.hostnameSuffix` | Sites answer at `http://<project>.<hostnameSuffix>[:sitePort]`. |
| `projects.dnnReleaseSources` | GitHub releases API URLs - the repositories **New project** offers, with their versions. |
| `projects.keepDnnPackages` | `false` by default. When `true`, each downloaded DNN install package is kept in `Documents\DnnManager\packages\<owner>.<repo>\` and used again when a new project picks the same version - no download. Each repository's version list is saved at every start (in `dnnmanager.db`, `dnn_releases`), so New project works without internet for the kept versions. When `false`, the package is downloaded into the project and deleted after installing. |
| `projects.dnnDefaults.*` | What **New project** starts with for a new site: `installMode` (`automatic` or `manual`), `hostUsername` (`host`), `hostEmail` (`admin@admin.com`; empty: `host@<hostnameSuffix>`), `websiteName` (`My Website`; empty: the project's name), `language` (`en-US`, `de-DE`, `es-ES`, `fr-FR`, `it-IT` or `nl-NL`) and `template` (`Default Website` or `Blank Website`). The host password is not in the settings: it is in the Windows Credential Manager of your account (`DnnManager/dnn-defaults/host-password`); while none is saved there it is `Admin@123`. Set in **Settings → Projects**. |
| `projects.keepWarm.*` | How the sites switched to [keep warm](user-guide.md#keep-warm) are kept warm: `pingMinutes` (`5`, 1 to 60) - the longest wait between two requests, shortened for a site whose app pool idles out sooner; `pingPath` (`/KeepAlive.aspx`) - the page requested to keep a running site warm (`/` keeps the home page's caches warm too, as Azure's *Always On* does); `warmUpPath` (`/`) - the page requested to warm up a site without a worker process. Pages are on the site itself (a leading `/` is added), never one of DNN's installer (`/Install/…`, `mode=`). Set in **Settings → Projects → Keep warm**. Which sites are kept warm is not in the settings: it is in `dnnmanager.db` (`keep_warm`). |
| `sqlServer.type` | Where a new project's database goes: `container` (the local SQL container - the default), `sqlServer` (SQL Server / SQL Server Express at `server`, with `authentication` `windows` or `sql` and, for `sql`, the login `userName` - its password is in the Windows Credential Manager, `DnnManager/database-server/password`) or `localDbFile` (the site's own `App_Data\Database.mdf` on the LocalDB instance `server`, e.g. `(LocalDB)\MSSQLLocalDB`). Set in **Settings → Database server**; a project keeps the database it was made with. |
| `sqlServer.host`, `port`, `userName`, `saPassword` | The local SQL container DNN Manager connects to: `host` (`localhost` by default), `port`, `userName` (the login it signs in with - `sa` when empty; another one has to exist on the container already - the same setting as the SQL Server login of `sqlServer.type` `sqlServer`) and `saPassword` (that login's password - and the `sa` password **Set up docker-compose** creates the container with). The password is stored encrypted for your Windows account (Windows DPAPI, `dpapi:…`) - not hashed, since DNN Manager needs it to sign in. Set in **Settings → Database server**. A new project's database is named like the project. The Docker container publishes SQL Server on this port with this password - on this PC only (`127.0.0.1`) when `host` is `localhost` or `127.0.0.1`, on every network interface for any other host (this PC's address on the network, for a VM). An existing data volume keeps the sa password it was created with. |
| `docker.*` | The SQL Server container: `containerName`, `volumeName`, `edition` (`MSSQL_PID`) and `collation`. **Settings → Docker container → Set up docker-compose** makes the container from these (and `sqlServer.port` / `saPassword`); **Show docker-compose.yml** shows the file to copy. |
| `ssms.rememberPassword` | `false` by default. When `true`, signing SSMS in from the project menu ticks its *Remember Password*, so SSMS keeps the password. On the Settings page under **Database server**. |
| `iis.requiredFeatures` | The Windows features **Settings → IIS → Test** checks and **Set up IIS** enables, each a `name` (as `dism /online /get-features` lists it) and a `label`. Set with **Settings → IIS → Edit…**: a row each, how it is shown and its Windows feature name, e.g. *ASP.NET 4.8* and `IIS-ASPNET45` (left empty, it is shown by its name). |
| `appearance.theme` | `system` (follow the Windows app theme), `light` or `dark`. Set from the gear's **Themes** (or the command palette's *Color theme…*), applied and saved at once. |
| `appearance.uiScale` | `100` by default: everything in the window - text, icons and spacing, menus and tooltips too - at this percentage (50 to 200; Settings offers 80-175). Set in **Settings → General**, applied at once. |
| `appearance.fontSize` | `13` by default: the app's text size in pixels (8 to 32; Settings offers 11-18) - titles and hints keep their proportions, icons keep their size. The terminal has its own (`terminal.fontSize`). Set in **Settings → General**, applied at once. |
| `appearance.projectColumns` | The optional columns the Projects table shows: `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`, `status`, `url`, `ports`, `id`, `size`, `memoryPercent`, `disk`, `network`, `path`. Set by the table's **Columns** button; the default is `url`, `dnn`, `database`, `sql`, `cpu`, `memory`, `pid`, `lastStarted`. |
| `terminal.*` | The terminal panel, set in **Settings → General** and applied at once: `defaultShell` (`powershell`, `pwsh`, `cmd` or `gitbash` - the first installed one when that one isn't), `fontFamily` (empty for Cascadia Mono, or Consolas) and `fontSize` (8 to 32) - also the font of the Output and Logs tabs. |
| `keyboard.shortcuts` | The keyboard shortcuts changed from their defaults, one row per command: `keyboard.shortcuts{project.start}` = `Ctrl+F5`, `keyboard.shortcuts{view.close}` = (empty) - an empty one takes the command's shortcut away; a command not listed has its default. Written as VS Code writes them (`Ctrl+Shift+P`, `Ctrl+,`, ``Ctrl+` ``, `Shift+F5`); a shortcut needs Ctrl or Alt, or a function key. Set in **Settings → Keyboard shortcuts**, applied and saved at once - see [Keyboard](user-guide.md#keyboard). |
| `layout.*` | How the window is laid out, as VS Code's **Customize Layout** sets it - applied and saved at once (see [Layout](user-guide.md#layout)): `sidebarPosition` (`left` or `right`), `panelAlignment` (`center` - under the page only; `justify` - the window's width; `left` / `right` - to that edge of the window, under the sidebar when it is on that side), `menuBarVisible` (`true` - for now the app's name in the title bar), `statusBarVisible` (`true`), `quickInputPosition` (where the command palette opens: `top` or `center`) and `density` (`default` - the sidebar, page and panel as rounded cards with a gap between them; or `compact` - flush, divided by lines, with a narrower sidebar (190 pixels, 40 with icons only), tighter entries and narrower title bar buttons). Whether the sidebar and the panel are shown is not here: it is the workspace's (`state`, area `window`). |
| `window.keepRunningWhenClosed` | `true` by default: closing the window hides it and DNN Manager keeps running, with an icon in the notification area to open it again or quit - see [Running in the background](user-guide.md#running-in-the-background). `false`: closing the window quits DNN Manager. Set in **Settings → General**, applied at once. |

### How the settings are saved

A key is the path to the value, each name in camelCase; a value is text,
written and read the same on every PC (`true` / `false`, numbers with a `.`).
Some of the rows of the default settings:

```text
key                                   value
version                               3
projects.baseDirectory                C:\DNN
projects.sitePort                     80
projects.keepDnnPackages              false
projects.dnnReleaseSources            2
projects.dnnReleaseSources[0]         https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases
projects.dnnReleaseSources[1]         https://api.github.com/repos/DNN-Connect/Dnn.Platform/releases
projects.keepWarm.pingMinutes         5
sqlServer.saPassword                  dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sB…
iis.requiredFeatures                  16
iis.requiredFeatures[0].name          IIS-WebServerRole
iis.requiredFeatures[0].label         IIS Web Server
appearance.fontSize                   13
keyboard.shortcuts                    0
layout.sidebarPosition                left
```

- **A list** has a row with how many items it holds
  (`projects.dnnReleaseSources` = `2`) and a row per item, numbered from 0
  (`projects.dnnReleaseSources[0]`); an item with values of its own has a row
  for each (`iis.requiredFeatures[0].name`).
- **A dictionary** (`keyboard.shortcuts`) has a row with how many entries it
  holds and a row per entry, its key in braces and `%`-escaped
  (`keyboard.shortcuts{project.start}`).
- **A setting without a row** has its default, and is saved with it at the next
  start - a setting a new version adds needs nothing from you.
- **Rows this version doesn't know** (ones a newer DNN Manager added) are kept
  when it saves; an item it removes from a list or dictionary goes.
- **The `sa` password** (`sqlServer.saPassword`) is encrypted for your Windows
  account (DPAPI, `dpapi:…`).

The rows are written by the app: one changed by hand that can't be read or isn't
allowed stops the next start (below).

## When the settings can't be used

When the app starts, it reads the settings - a setting without a row gets its
default (and is saved). **Settings it can't use** open a dialog that says what's
wrong:

- the database can't be opened or read (another program has it locked,
  Windows Security's *Controlled folder access* blocks it);
- the settings were saved by a *newer* DNN Manager (their `version` is higher
  than this version's);
- the `sa` password was encrypted by another Windows account or on another PC;
- a value can't be read (text where a number belongs) or isn't allowed (a site
  port of 0).

The dialog has **Try again** (once what it names is fixed), **Reset to
defaults** (the current settings aren't kept) and **Exit**. The app never
starts with half-read settings, and
nothing is written until they can be used. Settings from a newer DNN Manager
are not touched until you reset them - or update the app.

## Environment variables

Environment variables prefixed with `DNNMANAGER_` override settings, e.g.
`DNNMANAGER_DnnManager__Docker__SaPassword=...` (names as in `AppOptions`). The
Settings page lists any that are set, since they win over what it saves.

> The folder is the Documents folder of the Windows account the app runs as. If
> you sign in to the UAC prompt with a *different* administrator account, that
> account's Documents is used.
