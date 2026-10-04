# User guide

How to use DNN Manager: installing it, the window, every page and menu, the
settings, automatic DNN setup, importing, hosting and cloning sites, keep warm
and backups. New here? Start with the [README](../README.md).

**Contents**

- [Install](#install)
- [Using the app](#using-the-app)
- [Backups](#backups)
- [Automatic DNN setup](#automatic-dnn-setup)
- [Import a site .zip](#import-a-site-zip)
- [Host a project](#host-a-project)
- [Clone a project](#clone-a-project)
- [Keep warm](#keep-warm)
- [Notes / limitations](#notes--limitations)

Settings and their `settings.json` keys: [configuration.md](configuration.md). Problems and fixes: [troubleshooting.md](troubleshooting.md).

## Install

Run `DnnManagerSetup-<version>-x64.exe` (see [Build the installer](releasing.md#build-the-installer)).
Like the Visual Studio Code user installer, it needs no administrator rights:

- installs into `%LOCALAPPDATA%\Programs\DnnManager` - **Browse…** picks another folder
- adds **DNN Manager** to the Start menu, and a desktop shortcut if you tick it
- registers in **Settings → Apps → Installed apps**, where you uninstall it
- starts the app when you click **Finish** (the app asks for administrator
  rights itself - UAC - every time it starts, since it manages IIS)

The app is self-contained: no .NET runtime to install. When DNN Manager is
already installed, Setup's first page shows the installed version and asks what
to do: **Repair**, which installs this Setup's version over it
in the same folder, or **Uninstall**, which runs the uninstaller and closes
Setup. Your settings are kept either way. If DNN Manager is running, Setup (and
the uninstaller) asks you to close it first. Silent installs (`/SILENT`,
`/VERYSILENT`) skip that page and upgrade in place.

`Setup.exe /ALLUSERS` installs for every user into `Program Files` instead (it
asks for administrator rights). For unattended installs, Inno Setup's
`/SILENT` / `/VERYSILENT` and `/DIR="..."` work too.

Your settings and backups are **not** in the install folder - they're in
`Documents\DnnManager` (see [Configuration](configuration.md)). Setup never
writes there, so upgrading, reinstalling or uninstalling keeps them. To remove
them, delete that folder after uninstalling.

## Update

DNN Manager asks GitHub for its newest release a few seconds after it starts,
every hour after that, and when you open **Settings → About**. When there is a
newer one, a blue **Update** button appears in the title bar, left of the
layout buttons. Click it and DNN Manager:

1. downloads the release's file for your copy - the new Setup for an installed
   DNN Manager, the new portable exe for a portable one - the button stays
   **Update**, dimmed while it works, and its tooltip says what it is doing;
2. checks the download before it uses it: its size, the SHA-256 GitHub lists for
   it, and the version inside the file;
3. closes, saving your workspace as on any close (see
   [Picking up where you left off](#picking-up-where-you-left-off)) - it asks
   first only when an operation is running or a password on Settings isn't saved;
   **Stay** cancels the update and leaves everything as it was;
4. installs the new version: Setup runs over your installation with its progress
   window and no questions; a portable exe is replaced in place, under its own
   file name, so your shortcuts and *Start DNN Manager when you sign in* keep
   working;
5. opens again - the new version - where you were, with a message that says it
   updated (*Updated to v1.7.0 - you're back where you left off*).

Until DNN Manager closes nothing is changed: a download or a check that fails
leaves this version running and says why; **Update** tries again.
After it closes, a failed install starts the old version again and says why
(**Show log** opens the update's log). A new portable exe that closes with an
error right after it starts is swapped back for the old one. Your settings are
never touched - they're in `Documents\DnnManager`.

The button is disabled while an operation runs, and missing in a development
build (`dotnet build` output) - that is rebuilt, not updated. **Settings →
About** shows the status: *Checking for updates…*, *Up to date* (green dot),
*Update available* (orange), *Downloading update…*, *Installing update…*,
*Restarting…*, or *Unable to reach GitHub* (gray dot, with the reason under it).

## Picking up where you left off

DNN Manager opens the way you left it - after you close it, after **Restart** on
Troubleshoot, after an update, and after a crash. It keeps:

- **the window** - where it was, its size, maximized or not, the sidebar shown
  or hidden, the bottom panel's height and whether it is open or maximized;
- **where you were** - the page; on Projects the search, *Only show running*, the
  column the table is sorted by, the expanded and selected rows, how far it was
  scrolled, and the project whose Details were open with their tab; the Settings
  category, and Settings or Troubleshoot when one was open over the page;
- **what you typed and didn't use yet** - the **New project** and **Host project**
  forms, and unsaved changes on **Settings** (they come back unsaved, for
  **Save** or **Discard changes**);
- **the bottom panel** - which tab was shown (Output or Logs), the search, the
  site and log on the Logs tab, and how wide the terminal list is.

**Never kept:** the terminals - a shell can't outlive DNN Manager, so each start
begins without any (open one with **Ctrl+`**); passwords (type them again -
closing with a changed, unsaved password asks first), dialogs and questions,
running operations, messages. What
no longer exists falls back to the place above it: a removed project leaves you
on the Projects table (a message says so), a log that is gone shows the site's
newest one, a window place on a monitor that isn't there any more centres the
window. A project whose database doesn't answer opens as usual, saying so on its
Details.

It is saved a moment after anything changes and when DNN Manager closes, in
`Documents\DnnManager\state` (see [Configuration](configuration.md#where-your-files-are)) -
never in the program's folder, so updates don't touch it. **Reset to factory
defaults** on Troubleshoot forgets it; so does deleting that folder while DNN
Manager is closed.

## Using the app

### Window

- **Title bar** - DNN Manager draws its own. In the middle, as in VS Code, is
  the search: click it to open the [command palette](#keyboard) on the projects
  (**Ctrl+P**; type **>** for the commands). It narrows with the window and is
  gone in a very narrow one. Next to the minimize / maximize / close buttons are
  VS Code's layout buttons: **Customize Layout** (see [Layout](#layout)), then
  the sidebar and the bottom panel - each drawn filled in while it is shown; a
  click shows or hides it. Drag the title bar to move the window, double-click it
  to maximize; on Windows 11, resting on maximize shows Snap layouts.
- **Theme** - *Dark* and *Light*, with the colours of VS Code's *Dark Modern* and
  *Light Modern*; *System* follows the Windows app theme. Chosen from the
  gear's **Themes** or the command palette's *Color theme…* (applied and saved
  at once).
- **Sidebar** - the pages; at the bottom the **gear**. (The
  projects folder is in **Settings → Projects**.) The gear opens VS Code's *Manage* menu: **Command Palette…**,
  **Settings**, **Keyboard Shortcuts**, **Themes**, **Customize Layout…**,
  **Troubleshoot** and **Check for Updates…**. In a window narrower than 1100
  pixels the sidebar slides to a narrow one with only the icons (their names as
  tooltips). **Ctrl+B** (or the title bar's sidebar button) slides it closed,
  and open again. Drag its edge to make it
  wider or narrower - 160 to 600 pixels, kept between starts;
  **Customize Layout**'s reset puts it back.
- **Resizing** - the edges between the sidebar, the page and the panel can be
  dragged: three dots show where (in the Compact density, the lines between
  them), and the edge turns blue while you drag it.
  Where the sidebar's edge meets the panel's, drag both at once. The sidebar and
  the panel follow the pointer down to nothing, and out again from their edge -
  the window's edge, or the page's bottom for the panel. Let go smaller than its
  smallest size (160 pixels for the sidebar, 90 for the panel) and it slides out
  to it; under half of that, it slides closed. The sidebar's edge can't be
  dragged while it shows only its icons.
- **Window size** - like VS Code's, the window shrinks down to 400 × 270 pixels:
  the title bar's search goes when there is no room for it, the page keeps at
  least a strip above an open panel.
- **Settings and Troubleshoot** open over the page, like VS Code's modal
  editors: the page dimmed behind them, the title bar and status bar still
  working. Their **✕**, **Esc**, **Ctrl+W** or a click on the dimmed page closes
  them - Settings asks first when it has unsaved changes.
- **Status bar** - along the bottom of the window (Customize Layout can hide it),
  laid out like Docker Desktop's:
  - *under the sidebar*: **IIS running** / **stopped** in its colour (the IIS
    web service, W3SVC - Windows reports when it starts or stops, from wherever
    that is done) with **Restart** and **Stop**
    while it runs, **Start** while it's stopped (`iisreset`; stopping and
    restarting ask first - every site on the PC goes down). Restart is also what
    picks up IIS changes, e.g. a newly installed URL Rewrite module when a site
    shows *HTTP Error 500.19*. Under the sidebar it is as wide as the sidebar;
    with the sidebar narrow or hidden, as wide as what it shows;
  - *next*: this PC's **RAM** in use, **CPU** use (two decimals) and the
    **Disk** space used on the projects folder's drive, with its size as the
    *limit* - evenly spaced; the digits are all as wide, so a figure only
    moves when a number gains or loses a digit (measured every 2 seconds, the
    disk every 10);
  - *right*: the app's version.
- **The running operation** - a toast over the bottom-right corner, as VS Code
  shows a task in progress: its name, **Cancel**, and a bar moving along its
  bottom edge, from when it gets under way until it ends (one that asks first -
  Remove, Stop IIS - shows once you said yes). Click the name to open the panel
  on **Output**. **Cancel** puts everything back
  as it was before the operation started: what it made is taken away again,
  the last first - the IIS site and app pool, the database, the files and
  folders, an edited `web.config` - each step shown in **Output**. What can't
  be put back (a database or site that was there and was replaced as you
  chose, or what **Remove…** had already deleted) is named there.
- **Bottom panel** - opened and closed with the title bar's panel button,
  **Ctrl+J**, or **Ctrl+`** for the terminal (see [Keyboard](#keyboard); it comes
  back at the height it had) - it slides open and closed. Drag the edge above it
  to make it taller or lower. Like VS
  Code's: its tabs on the left of its header - **Output**, **Logs**,
  **Terminal** -, then new terminal (on Terminal), clear (on Output), search,
  maximize and hide on the right. Copy and paste are **Ctrl+C** / **Ctrl+V** and
  the right-click menu, on every tab. The **maximize** button (or **Ctrl+Shift+M**) gives the panel the page's
  room - the tabs stay, the sidebar and status bar too -; again restores it.
  - **Output** - what DNN Manager does, operation by operation, stage by stage
    (the *Pipeline Rail*). It comes to the front when an operation starts, or
    when you click the running operation's toast. A red dot next to
    its name says the last operation failed - also while another tab is shown -
    until you open it, or the next operation starts.
    - *Header*: the newest operation's status (**RUNNING** with a spinner,
      **FINISHED**, **FAILED** or **CANCELLED**), its title and what it works
      with (the SQL Server, the site's address), then its warnings or errors,
      when it started and ended, and how long it took - counting up while it runs.
    - *Stages* (left - drag its right edge to make it wider or narrower): each stage of the newest operation with its duration - a
      tick when it went well, a triangle when it warned, a cross when it failed,
      a spinner while it runs; the ones still to come grey, and those it never got
      to after a failure *skipped*. Under them **Background**: the sites
      [kept warm](#keep-warm), each with how long it took to answer last (its
      messages go to the log file, not between the operations). At the bottom
      the operation's summary: files copied, warnings, errors.
    - *Log* (right): every operation, grouped by stage - each stage's title and
      duration, then its lines with their times (to a tenth of a second). Values
      such as databases, servers, host names and addresses stand out (an address
      opens with a click), file paths are dimmed and shortened to under your
      profile (the whole path on hover). A warning has a yellow **WARN** label, an
      error a red **ERROR** label with what lies behind it and what to do under it. Each operation ends with
      a line of its own: a green **SUCCESS** label with what it did (*Removal
      complete*) and the site's address to open, a red **ERROR** label with where
      it stopped, or **CANCELLED** - each with how long it took. Lines that belong to no operation - a site stopped in
      IIS Manager - stand between them. It follows the newest line while you are
      at the bottom; scrolled up, it stays put. Read-only: select text across
      lines and copy it. **Clear** empties it (an operation that is running
      stays); search (**Ctrl+F**) looks through every stage.
  - **Logs** - a website's logs, one at a time: choose the site (every IIS
    site, by name) and the log at the top (the log's path, size and time next
    to them, with a button to open its folder). **View logs** on a site's right-click menu opens this tab with that
    log. Its newest 5,000 lines are read - never the whole of a large file -,
    then new lines appear as they are written (every second); the view follows
    them while it is at the bottom. Errors are red, warnings yellow. Long lines
    wrap at the panel's width (after the last space that fits), so there is no
    scrolling sideways; copying gives the lines without those breaks. Select text
    as in an editor - drag (across lines; past the edge it scrolls), Shift+click
    to extend, double-click a word, triple-click a line, **Ctrl+A** - and copy
    it with **Ctrl+C** or the right-click menu. The logs:
    *DNN* (`Portals\_default\Logs`, the newest 8), *IIS* (the site's request
    logs, the newest 8, and HTTP.sys's error log), *Windows* (ASP.NET errors and
    warnings about the site, its app pool's events, worker process crashes - from
    the event logs, new entries as they are logged).
  - **Terminal** - only shells; the open ones are listed on the right, each
    with its shell's icon - drag the list's left edge to make it wider or
    narrower; a terminal's bin shows while the mouse is on it. A click (or the
    arrows) shows that terminal and keeps the keyboard in the list - outlined in
    blue; a click on the list's empty part does that for the terminal shown:
    **Del** closes it (and the next **Del** the next one), **F2** (or a
    double-click) renames it - **Enter** takes the name and the keyboard stays in
    the list -, **Enter** or a second click on it puts the keyboard in the
    terminal to type (as does a click in the terminal). Drag a terminal - or
    **Alt+↑** / **Alt+↓** - to change the order. Terminals aren't kept when DNN
    Manager closes: their shells end with it. Opening the tab
    with none open starts one. **+** opens one with the default shell; the arrow next to
    it offers the shells installed on this PC: **PowerShell** (the default),
    **PowerShell 7**, **Command Prompt** and **Git Bash**. They start in the
    projects folder - or in a project's folder with **Open in terminal** on its
    right-click menu - and run with Administrator rights, like DNN Manager. They
    are real terminals (Windows' pseudo console): colours, tab completion and
    full-screen programs work. The scrollbar, the mouse wheel or **Shift + Page
    Up / Down** scroll back through the output, and typing jumps to the newest
    line again; drag to select, **Ctrl+C** copies a selection (and interrupts
    the program when nothing is selected), **Ctrl+V** or a right-click pastes.
    Rest the mouse on a tab for what it is: its shell, process ID, program
    (the `.exe`), the folder it started in and when. **Double-click** a tab (or
    **F2**, or right-click → **Rename**) to give it a name of your own. The
    **bin** on a tab - or `exit` - ends its shell; closing the panel doesn't.
  - **Search** - **Ctrl+F** (or the magnifier) opens a search bar over the shown
    tab: every match highlighted, the current one selected, *3 / 18* how many
    there are. In the box, like VS Code: **Aa** Match Case (**Alt+C**), **ab**
    Match Whole Word (**Alt+W**) and **.\*** Use Regular Expression (**Alt+R**) -
    a pattern that isn't valid says *Invalid regular expression* (why, in its
    tooltip). **Enter** / **F3** goes to the next one, **Shift+Enter** /
    **Shift+F3** to the previous one (around at the end); it starts at the
    newest. New output is searched as it comes, without jumping away from the
    current match. **Esc** or **✕** closes it and takes the highlights away -
    the text, and what runs in a terminal, aren't touched.
  The default shell, the font and its size, and whether terminals are offered
  at all (without them the Terminal tab is hidden) are in **Settings → General**.
- **Dialogs** - questions from an operation (confirmations, e.g. before dropping
  a database) open as dialogs.
- **One DNN Manager at a time.** Starting it while it's already open (Start
  menu, shortcut, the exe) brings the open window to the front - restored if it
  was minimized - instead of opening a second one, and doesn't ask for
  Administrator rights again.
- Only one operation runs at a time. While it runs, the pages stay usable
  (scrolling, browsing), but starting a second one is refused.
- **Pages are kept** while the app runs: the Host project folders and the DNN
  versions load once, not on every visit. Their **Refresh** button loads them
  again, and so does every finished operation (a page not on screen catches up
  when it's next shown). **Projects** has no Refresh - it keeps itself up to
  date (see [Projects table](#projects-table)). Settings is read on every visit.
- **Toasts** - short messages over the bottom-right of the window, e.g. why an
  operation failed (with **Show output**, which opens its log) or why the
  settings couldn't be saved. They fade out by themselves; warnings and errors
  stay until closed.

### Layout

**Customize Layout** - the first of the title bar's layout buttons, the gear's
menu, or the command palette - lists VS Code's layout choices in one list that
stays open: each choice applies at once and is saved (`layout` in
[settings.json](configuration.md#settingsjson)), the current one checked. Type to
narrow the list; **↺** next to its name puts the defaults back.

| Group | Choices |
|---|---|
| **Visibility** | **Menu Bar** (for now the app's name in the title bar), **Sidebar** (**Ctrl+B**), **Panel** (**Ctrl+J**) and **Status Bar**, shown or hidden. With the sidebar hidden, the IIS cell of the status bar keeps its state and buttons. |
| **Sidebar Position** | **Left** (the default) or **Right**. |
| **Panel Alignment** | How far the bottom panel reaches: **Center** (the default) - under the page only, the sidebar full height beside it; **Justify** - the window's whole width, under the sidebar too; **Left** / **Right** - to that edge of the window, so under the sidebar when it is on that side. |
| **Quick Input Position** | Where the command palette opens: **Top** (over the title bar's search, the default) or **Center**. |
| **Layout Density** | **Default** - the sidebar, the page and the panel as rounded cards with a 5-pixel gap between them and along the window's sides, right under the title bar and down to the status bar - one surface with them, without lines, as VS Code's. **Compact** - the sidebar, the page and the panel flush, divided by lines, and the frame narrower: a narrower sidebar (190 pixels, 40 with icons only) with tighter entries, narrower title bar buttons. The title bar and the status bar are as low in both. |

Whether the sidebar and the panel are shown is kept with the workspace
([Picking up where you left off](#picking-up-where-you-left-off)); the rest is a
setting.

### Keyboard

DNN Manager works without a mouse, the way VS Code does.

- **Moving around**: **Tab** / **Shift+Tab** go from control to control, the
  **arrows** move in lists, the Projects table, menus and drop-downs, **Enter**
  opens or runs what is selected (a project row opens its Details), **Space**
  ticks check boxes and switches (and a project row's check box), **Esc** closes
  the palette, menus, drop-downs, the Details, Settings and Troubleshoot. Tab
  enters the Projects table once and leaves it with the next Tab - the arrows move
  inside it. A click on a row - its name, address or a button too - selects it
  and puts the keyboard there, so the arrows go on from it, as in the terminal
  list; **→** shows the selected project's details in the table (as its **›**),
  **←** hides them; **Del** removes the selected project (asking first). A click in the
  table's empty space puts the keyboard on its first row; when the keyboard
  leaves the table, no row stays selected. Where the keyboard is shows as a blue ring (only when the keyboard
  moved there, not on a click).
- **The command palette** - **Ctrl+Shift+P** lists every command that makes sense
  now (*Start project* only for a stopped one…), with its shortcut; type to
  narrow it down, the arrows choose, **Enter** runs, **Esc** closes and the
  keyboard goes back where it was. **Ctrl+P** lists the projects instead - Enter
  opens one's Details; typing **>** switches to the commands, deleting it back.
  A project command acts on the selected project (the table's selected row, or
  the open Details); without one the palette asks which project.
- **Tooltips** - resting the pointer on a button or a field shows its name, as
  in VS Code, with its shortcut when it has one - as set now, so a changed
  shortcut shows there at once.
- **Shortcuts** (the defaults - change them in **Settings → Keyboard shortcuts**):

  | Shortcut | Does |
  |---|---|
  | **Ctrl+Shift+P** / **Ctrl+P** | Command palette / go to a project |
  | **Ctrl+,** | Settings - a second time closes it |
  | **Ctrl+B** | Show or hide the sidebar |
  | **Ctrl+F** | Search in the view: the panel's tab when it has the keyboard, else the Projects or Settings search |
  | **Ctrl+Shift+F** | Search projects (the Projects table's search box) |
  | **F5** / **Shift+F5** / **Ctrl+Shift+R** | Start / stop / restart the selected project |
  | **Ctrl+1** / **Ctrl+2** / **Ctrl+3** | Projects / New project / Host project |
  | **Ctrl+Tab** / **Ctrl+Shift+Tab** | Next / previous page |
  | **Ctrl+PageUp** / **Ctrl+PageDown** | Next / previous tab - the Details' tabs, the Settings categories, the panel's tabs (when it has the keyboard) |
  | **Ctrl+W** | Close the view: Settings or Troubleshoot, back to the page under them; the Details back to the table |
  | **Ctrl+J** | Show or hide the bottom panel |
  | **Ctrl+`** / **Ctrl+Shift+`** | The terminal (again in it: hides the panel) / a new terminal |
  | **Ctrl+Shift+U** | Show Output |
  | **Ctrl+Shift+M** | Maximize or restore the panel |

  The palette also has, without a shortcut until you give them one: open the
  website, the project folder, its logs, a terminal in its folder, keep it warm
  (or stop), open its Details, clear the output, run troubleshooting, the
  keyboard shortcuts, the color theme, Customize Layout, the status bar, check
  for updates, install the update, restart DNN Manager.
  While a **terminal** has the keyboard, its keys go to the shell - only the
  palette (Ctrl+Shift+P, Ctrl+P), Settings, Ctrl+F, Ctrl+B, the panel and
  terminal keys (Ctrl+J, Ctrl+`, Ctrl+Shift+`, Ctrl+Shift+U, Ctrl+Shift+M) are
  DNN Manager's, as in VS Code.
- **Settings → Keyboard shortcuts** lists every command with its shortcut and
  the part of DNN Manager it belongs to, with a search (by action, part or key).
  Select a shortcut - or press Enter on it - and press the new keys; **Esc**
  cancels, **Backspace** removes it. A shortcut needs Ctrl or Alt, or a function
  key (plain keys are for typing and moving around). Two commands on one
  shortcut are flagged (the first listed runs), and so is a text box's editing key
  (Ctrl+C, Ctrl+V…). The ↺ button puts one back to its default, **Reset all to
  defaults** all of them. Changes apply and are saved at once, in `settings.json`
  (`keyboard.shortcuts` - see [Configuration](configuration.md)), so they are kept
  through restarts and updates.

### Efficiency mode (while the window can't be seen)

A window that can't be seen - minimized, or completely covered by other windows,
on another virtual desktop or behind a locked screen - shows nothing, so DNN
Manager stops spending on it - **Settings → General → Save resources while the
window can't be seen**, on by default. Whether it is covered is decided as
Chromium apps such as Docker Desktop and Edge decide it (Chromium's
`NativeWindowOcclusionTracker`): while the window isn't in front, Windows' window
events have it look again 100 ms after they settle, and only opaque, ordinary
windows count as covering it (not tool windows, see-through or click-through
ones, or ones of an irregular shape).

- **Paused** - the progress bars and a changing site's pulsing dot (WPF draws
  about 60 frames a second for them, seen or not), this PC's figures in the
  status bar, drawing a terminal (its output is still read, so the shell never
  waits), following a log file, and folder-size walks. A toast that comes
  meanwhile waits to be seen before it fades.
- **Not paused** - what Windows reports about IIS and the projects folder, the
  sites' reconciliation every 30 seconds (a site stopped meanwhile is noted in
  **Output**), [keep warm](#keep-warm)'s requests, operations with their progress
  and log lines, the log file.
- **Efficiency mode** - once the window hasn't been seen for a second, no
  operation runs (or ended in the last 5) and no terminal printed for 10,
  DNN Manager goes into Windows' *Efficiency mode* - EcoQoS and Idle priority,
  as Task Manager sets it, which shows the leaf next to it. Terminal shells
  always start at Normal priority, and anything opened from the window is
  opened after it has ended. It ends at once when an operation
  starts, a terminal prints or the window can be seen again, so operations
  always run at full speed. Task Manager also writes *Efficiency mode* in the
  Status column, because DNN Manager's own process is in it; Chromium apps only
  show the leaf there because they put their child processes in it, never the
  main one - DNN Manager's work is in its main process.
- **Seen again**, everything is brought up to date at once, without a loading screen:
  the table reads what changed, the figures come within about a second, a log
  shows every line written meanwhile, a terminal is drawn again.

Turned off, everything goes on while the window is minimized, as when it is
shown. On Windows 10 the request gets the milder *low QoS*; on battery, Windows
also slows minimized apps down by itself.

### Pages

| Page | What it does |
|---|---|
| **Projects** | A table of every DNN website in IIS, one row per site - see [Projects table](#projects-table) and [Site overview](#site-overview). |
| **New project** | Enter a name (validated as you type), then **Start from**: *a new site* - pick the **Repository** (e.g. `dnnsoftware/Dnn.Platform`), then a **Version** from its GitHub releases (highest version first; pre-releases are listed too, marked *(pre-release)*, but the latest release - marked *(latest)* - is what's selected; *kept, no download* marks a version whose package is kept). The lists are loaded once, when the app starts - the refresh button next to Version asks GitHub again - or *an existing site* - pick the site `.zip` and its database `.bacpac` (see [Import a site .zip](#import-a-site-zip)). A name whose folder already exists is refused - set up an existing folder on **Host project**. For a new site: **IIS** - the host name and port it answers on; **DNN installation** - *Automatic setup* (the default) or *Manual DNN setup*; **DNN account and website** (automatic setup) - host username and password (**Generate** makes one), e-mail, website name, language and site template, filled in from **Settings → Projects → DNN defaults**. **Database** - filled in from **Settings → Database server** (connection type, server, authentication, username and password) and named like the project; change any of it here for this project only (**Use the settings** fills it in again - the settings stay as they are). A LocalDB file is the site's own `App_Data\Database.mdf`. It is tested before anything is created. Host name and website name follow the project's name until you type your own. **Create project** is ready once everything is valid. See [Automatic DNN setup](#automatic-dnn-setup). |
| **Host project** | Pick a folder, then *IIS website + database* (the default), *database only* or *IIS website only*, and optionally a backup to restore. See [Host a project](#host-a-project). |
| **Test and set up** (Settings → Docker container, Database server and IIS) | A card at the end of each of those categories, checked when you press its **Test** button (nothing runs on opening it; an action re-tests what it changed), with a green / red status and a button to fix it. **Docker**: **Docker Desktop** (**Install Docker Desktop** via winget), the **Docker engine** (**Start Docker Desktop**, then waits for the engine) and the **SQL Server container**. **Set up docker-compose** runs the docker-compose.yml made from the settings (`docker compose up -d`, handed to Docker directly - no file is written, and it has the real SA password): it creates the container, starts it, or updates it after the settings changed, then waits for the sa login. The compose project is `dnn-mssql`; a container made by an older DNN Manager under `dnn-shared` is removed and made again under the new name - the databases stay, they're in the volume. **Show docker-compose.yml** shows the same file with a **Copy** button, to run yourself - without the SA password: replace `<your-sa-password>` after copying. **Database server**: the server from **Settings → Database server**, whichever connection type it is - it answers, the sign-in works, its version, and whether the login may create the databases new projects get. **IIS**: the Windows features as a table with their status. **Set up IIS** checks them and, after a confirmation, enables the missing ones (a reboot may be needed). Restarting IIS is on the status bar. |
| **Settings** (the gear's menu at the bottom of the sidebar, or **Ctrl+,** - a second time closes it) | Opens over the page and edits `settings.json`, laid out like Docker Desktop's settings: the categories on the left, under a **search** box that leaves the ones with a matching setting, and the chosen category on the right. **General**: **Start DNN Manager when you sign in** (a scheduled task that starts it with its Administrator rights, so Windows doesn't ask for them at every sign-in), the **UI scale** (80-175 %, everything bigger or smaller like a browser's zoom) and **font size** (11-18 px, only the text), **Save resources while the window can't be seen** (see [Efficiency mode](#efficiency-mode-while-the-window-cant-be-seen)), and the **terminal** (on or off, the default shell, font family and size) - putting every setting back to its default is on **Troubleshoot**. **Projects**: the projects folder, hostname suffix and site port, the **DNN defaults** new projects start with (install mode, host username and password, e-mail, website name, language, site template - the password is kept in the Windows Credential Manager, not in `settings.json`), and **Keep warm** - the interval, keep-alive page and warm-up page of the sites kept warm (see [Keep warm](#keep-warm)). **DNN releases**: the repositories, and keeping downloaded packages. **Database server**: the **connection type** new projects get their database on - the *Local SQL container (Docker)* (host, port, SA password), *SQL Server / SQL Server Express* (server, Windows or SQL Server authentication, login - its password kept in the Windows Credential Manager) or a *SQL Server Express LocalDB (file)* (the LocalDB instance); only the chosen type's settings are saved, the others keep what they had - and remembering the password in SSMS. **Docker container**: its name, volume, edition and collation. **IIS**: the Windows features DNN Manager needs (the list is edited in the file). **Keyboard shortcuts**: every command's shortcut, to search, change and reset - saved at once, no Save needed (see [Keyboard](#keyboard)). **About**: which DNN Manager this is (version, commit, build date, program folder, release channel, the update status - see [Update](#update) -, license, repository and documentation as links), what it runs on (.NET, architecture, Windows, Administrator or not) and works with (IIS, .NET Framework, Docker, its IIS and SQL libraries), then the folders with your files (settings, backups, logs, DNN packages), each with **Open**. Nothing is saved until **Save** (bottom right, ready once you change something in any category; **Discard changes** puts the saved values back), which saves the settings and applies them at once - no restart. **Close** (or the ✕, **Esc**, or a click on the dimmed page) goes back to the page under it. **Docker container**, **Database server** and **IIS** each end with their **Test and set up** card - **Test**, **Set up docker-compose**, **Set up IIS** and the rest - working with the saved settings (while there are unsaved changes they wait for **Save**). See [Configuration](configuration.md). |
| **Troubleshoot** (the gear's menu, or the command palette) | Opens over the page, like Settings - **✕**, **Esc** or a click on the dimmed page goes back to the page under it - laid out like Docker Desktop's: **Restart** closes DNN Manager (asking first, as on any quit) and starts it again - projects, settings and data are kept. **Clean up data** deletes what is ticked from `Documents\DnnManager`, each with its size: the logs, the kept DNN packages, the settings copies and - never ticked for you - the project backups. **Reset settings to defaults** puts every setting in every category back as DNN Manager is installed - the saved passwords and starting at sign-in too - and applies them at once, without a restart (the old `settings.json` is copied to `backups`; the logs, kept packages and which sites are kept warm stay). **Reset to factory defaults** puts DNN Manager back as it was installed: the settings (a copy is kept in `backups`), the saved passwords, starting at sign-in, which sites are kept warm, the logs and the kept packages go, then it restarts. Your projects - their IIS sites, folders and databases - and their backups are never touched. Not while an operation runs. |

### Projects table

Every DNN website configured in IIS is a row - IIS is what the table shows, not
the projects folder; a site whose folder has no DNN install (`bin\DotNetNuke.dll`),
like IIS's own *Default Web Site*, isn't listed. The projects folder is only where **New project** and **Host
project** set sites up (and the only place **Remove…** deletes files from). Each
row: a **check box**, a **chevron** that opens the row's details (address,
physical path, app pool, bindings, site ID, database, DNN version, worker
process, keep warm), its **status** dot, its **name** (a link to the site's
[overview](#site-overview)), the columns you chose and its **Actions**. The
address is IIS's: an https binding with a certificate first, then http. The DNN
version and the database come from the folder the site serves (`bin\DotNetNuke.dll`
and its `web.config`) - a site that isn't DNN shows *(none)* and *-*.

- **Always current** - the table has no Refresh button: it follows the
  system. A site started or stopped in IIS Manager, IIS restarted, a worker
  process that ended, a site added or removed in IIS - each shows up by itself,
  in the row it is about, within seconds. Nothing else
  is touched: the checked rows, the search, the sort order, the scroll position
  and the open details stay as they are, and the only *Loading projects…* is
  the one when the app starts. Changes made outside DNN Manager are noted in
  the activity log. Only when something can't be read (IIS's configuration,
  the projects folder), or after the PC wakes up, *Reconnecting…* shows top
  right: the table stays as it was and catches up by itself. Rest the mouse
  on it for the reason and the time of the last synchronisation. How it works
  is under [Live updates](architecture.md#live-updates).
- **Status** - a green dot while the IIS site runs, a hollow ring while it's
  stopped (also when only its app pool is: *App pool stopped*), amber while
  it's starting, stopping or an action on it runs, a dash when the project has
  no IIS site.
- **Actions** - **Stop** and **Restart** for a running site, **Start** for a
  stopped one, the **flame** that switches [keep warm](#keep-warm) on and off
  (its look says how it is going), and **Remove…** (the bin) for any
  project - the site's tools (see below) are on its right-click menu. Stop also stops the
  site's app pool (unless another site uses it), which ends its worker process;
  Restart recycles the app pool; Start starts the pool and the site. The row
  says *Starting…*, *Stopping…* or *Restarting…* at once (*Removing…* once you
  have confirmed), with a progress bar, and every action waits for it (one
  operation runs at a time); afterwards it shows what IIS reports. A stopped
  site stays *Stopping…* until its worker process has ended - only then can it
  be started again. When an action fails, the row goes back to its real state
  and a toast says why. When IIS itself is stopped, start it from the status
  bar first.
- **Check boxes** - check rows (or press **Space** on the selected row) and the
  bulk actions appear above the table as one group of icons, in the order of a
  row's Actions: **Start**, **Stop**, **Restart**, the **keep warm** flame, then
  **Remove…** (the red bin, on the right) - rest the mouse on one for what it will do. Each acts on the
  checked rows it applies to - Start on the stopped ones, Stop and Restart on the
  running ones, keep warm on those with an address to request - and is greyed out
  when none of them qualifies. The flame is outlined while some of them aren't kept
  warm (a click keeps them all warm) and filled once they all are (a click stops
  keeping them warm). **Remove…** asks once for all of them, listing
  each one's database - which goes with it. The header check box checks every
  row the search shows, or none; it shows a dash when some are checked. Under
  the table: *12 projects* (or *4 of 12 projects* while searching) and
  *Selected 2 of 12*.
- **Search** - filters as you type on the name, site URL, site ID, port,
  status, DNN version, database and path; the **✕** in the box (or **Esc**)
  clears it. The bulk buttons
  act only on checked rows the search shows.
- **Only show running** (the switch next to the Columns button) - leaves the
  projects whose site is running; a row goes when its site stops and comes
  back when it starts.
- **Columns** (the button next to the search) - switch the optional columns on
  and off. They are listed, and shown, the most used first: *DNN version*,
  *Database* (the one the site's `web.config` names; *not set* in amber, with
  why in its tooltip, when a DNN site's `web.config` is missing, can't be read,
  has no `SiteSqlServer` or still has DNN's own connection), *SQL* - asked every
  10 seconds with the site's own `web.config` connection (its server and login,
  or Windows authentication as you), never DNN Manager's settings: **Live** in
  green when it answers and the database is there, **Offline** in red when it
  doesn't answer (why - e.g. *Login failed* - in the tooltip), *(none)* when the
  database isn't on that server, *File* for a LocalDB file the site attaches
  itself, *CPU (%)* (share of the whole PC), *Memory usage*, *PID*
  (what a debugger attaches to) and *Last started* (when the worker process
  started - IIS starts one on the site's first request) - these seven are the
  default, and **Default** in the menu goes back to them. Then *Status* (as
  text), *Site* (URL), *Port(s)*, *Site ID*, *Size* (the folder's size - measured only while the
  column is shown, it walks every file), *Memory (%)*,
  *Disk read/write* (bytes the worker process read and wrote since it started -
  its files, and its database connection), *Network I/O* (bytes the site
  received / sent over HTTP since IIS started - IIS's own counters) and *Path*.
  The choice is saved in `settings.json`. The CPU, memory, disk and PID columns
  show *-* while the site has no worker process.
- **What the table shows is the site as it is now**, read from IIS (folder,
  bindings, app pool, state), the site's folder (DNN version from
  `bin\DotNetNuke.dll`), its `web.config` (database, server, authentication)
  and its database (portals, host accounts) - never from what DNN Manager
  saved when it set the project up: those settings are only used to create a
  project. Change a binding, the app pool, the folder or the database in
  `web.config` outside DNN Manager, add a portal - the table and an open
  overview follow by themselves (IIS at once, the folders within 30 seconds).
  What can't be read says why, instead of an old value.
- The check box, chevron, status and name stay on the left and **Actions** on
  the right while the columns between them scroll sideways - scrollbar or
  **Shift + mouse wheel** - so a narrow window still shows every row's actions. Click a
  column header to sort; the table stays sorted as values change - a row whose
  status or CPU use changes moves to its place. Click a site's **name**,
  **double-click** its row or press **Enter** to open its
  [overview](#site-overview); **right-click** it for everything else (see
  [Project menu](#project-menu)).

### Site tools

On a site's right-click menu (and the **⋮** on its overview):

- **Clear website cache…** - after a confirmation, deletes DNN's cached files
  (`Portals\_default\Cache`) and bundled CSS / JavaScript
  (`App_Data\ClientDependency`) - the folders stay; files in use are left - and
  recycles the site's app pool, which empties what it holds in memory. For a
  site that isn't DNN only the app pool is recycled. The row says *Clearing
  cache…* meanwhile, a notification says when it's done (or why it failed); the
  table isn't reloaded.
- **View logs** ▸ - the site's logs under their kind (*DNN*, *IIS*, *Windows*;
  the path in the tooltip). One opens on the bottom panel's **Logs** tab, not in
  a terminal.

### Site overview

Opened from the table, in its place (**←** or **Esc** goes back, with the table
as it was). At the top the site's name, state and address, with **Start** /
**Stop** / **Restart**, **Open site**, **Open folder** and **⋮** (the [site
tools](#site-tools)) - all live, like the table. Below, five tabs, one shown at
a time. They show what was **detected** - read from IIS, the app pool, the
site's folder, `web.config`, `bin`, its database (with the connection
`web.config` has) and this PC - not what DNN Manager set up. Each card says
where its values come from; a dot marks a value worth a look (amber) or wrong
(red), with a line under it saying why. Values can be selected and copied;
addresses are links. Nothing on these tabs changes the site, apart from keep
warm and the host password. No password or key is ever shown.

- **General** - the **Project** (folder, size, type, DNN version from `bin`,
  when it was installed, last deployed to `bin`, `web.config` changed, git
  branch, solution, backups), **Project health** (DNN installation,
  `web.config`, the database configuration and whether it answers, the IIS
  site, the app pool, the portals and their aliases, how many issues were
  detected).
- **IIS** - the **Site** (ID, state, physical path, app pool, auto-start,
  preload), its **Bindings** (protocol, host as a link, port, IP, the
  certificate and when it expires - expired or within 30 days is marked -,
  SNI; a DNN portal alias with no binding is marked too), the **Application
  pool** (state, .NET CLR, pipeline, identity and its account, start mode,
  idle time-out, recycling, memory limits, queue length, 32-bit, rapid-fail
  protection, load user profile, worker processes), the running **Worker
  processes** (PID, up since, memory, CPU) and **Keep warm** (see [Keep
  warm](#keep-warm)).
- **DNN** - the **Installation** (version from `bin` and from the database -
  marked when they differ -, `InstallVersion`, installed and last upgraded,
  who set it up, scheduler, friendly URLs, caching, authentication providers,
  event log, debug mode, auto-add portal alias), the **Portals** (ID, name,
  primary alias as a link, other aliases, language, home directory), the
  **Extensions** (modules, scheduled jobs, packages by type), DNN's
  **configuration** in `web.config` (compilation debug, custom errors, machine
  key set or not, friendly URL and data providers, session state,
  authentication, auto upgrade) and the **Host accounts** - username and
  e-mail -, with **Change host password…** (see [Changing the host
  password](#changing-the-host-password)).
- **Database** - the **Connection** from `SiteSqlServer` (server, database,
  authentication and login name - never the password -, encrypt, trust server
  certificate, time-out, MARS, whether it answers), the **SQL Server**
  (instance, version, edition, collation), the **Database** (state, access,
  size and files, recovery model, compatibility level, collation - marked when
  it differs from the server's -, auto shrink / close, snapshot isolation,
  page verification, created, the login it is read with), the **DNN
  database** (version, object qualifier, portals, users, roles, scheduled
  jobs, event log) and **Database health**. Read-only; when `web.config` names
  no database, it says why.
- **Advanced** - the **Configuration** of `web.config` (target framework,
  request validation and filtering, compression, static content cache, HTTP
  errors, managed modules, rewrite rules, the HTTPS redirects DNN Manager
  switched off, binding redirects, probing path, sections kept in files of
  their own), the **Limits** (DNN's upload limit, `maxRequestLength`,
  `maxAllowedContentLength`, the **effective upload limit** - the smallest of
  them - and the execution time-out), the **Filesystem** (whether the app
  pool's account can write to the site's folder, `bin`, `App_Data`,
  `Portals`, `DesktopModules` and `Providers`), the **Assemblies** in `bin`
  (count, DNN's own, duplicates, a redirect to a version `bin` doesn't have,
  a missing reference) and every **Detected issue** in one list.

### Project menu

Right-click a project on the **Projects** page (or select it and press the
Menu key):

| Item | What it does |
|---|---|
| **Start** / **Stop**, **Restart** | The same as the row's actions, for the site's current state. |
| **Keep warm** / **Stop keeping warm** | Switches [keep warm](#keep-warm) on or off for the site - the same as the flame in its row. |
| **Open with…** ▸ | A submenu with only what is installed on the PC (greyed out when there is nothing). First the editors - Visual Studio (via `vswhere`; opens the project's `.sln` when it has exactly one), VS Code, VS Code Insiders, Cursor, Windsurf, Rider, IntelliJ IDEA, Sublime Text, Zed, Vim (gVim, or console Vim in a window of its own) and Neovim (nvim-qt, or `nvim` in its own window) - found in their usual install folders or on PATH. Git for Windows' bundled vim doesn't count. Then one entry per installed SQL Server Management Studio, each a submenu (21+ found via `vswhere`, 18-20 by their install folder): *Default* signs in to the local SQL Server from Settings as `sa`; *Project* signs in to the project's database - the one its `web.config` uses, with its login (or Windows authentication); greyed out, with why in its tooltip, when `web.config` names none. SSMS only remembers the password when **Remember the password in SQL Server Management Studio** is on in Settings (off by default). When that SSMS is already open, the connection is added to it (via its *Connect Object Explorer...*) instead of starting another window. For the local container it also trusts the self-signed server certificate (`-C`, SSMS 21+). SSMS takes no password on its command line - and any connection switch makes it connect at once and fail - so for SSMS 21+ DNN Manager starts it without switches and fills in its Connect dialog through UI Automation (server, SQL Server Authentication, login, password with *Remember Password*, database, trust certificate, name) and clicks Connect - only in the SSMS it just started. Older SSMS gets the switches, and the password is left on the clipboard. A missing database opens the server instead. |
| **Details…** | The site's [overview](#site-overview) - the same as clicking its name. |
| **Open site** / **Open folder** | The site in the browser / the folder in Explorer. |
| **Open in terminal** | A new terminal (the default shell) in the project's folder, in the terminal panel. Not shown when the terminal is switched off in Settings. |
| **Clear website cache…**, **View logs** ▸ | The [site tools](#site-tools). |
| **Clone…** | A copy of the project - its files, its database and an IIS website of its own - under a new name. See [Clone a project](#clone-a-project). |
| **Export** ▸ | A backup into the project's folder in `Documents\DnnManager\backups` (see [Backups](#backups)): *Site and database* (`<project>.zip` + `<project>.bacpac`, the pair **New project** imports), *Site files* or *Database*. **Open backups folder** opens it in Explorer. |
| **Remove…** | After a confirmation, removes the IIS site and - for a site in the projects folder - deletes its folder, and drops its database - always, with one confirmation that names it. Only a database on this PC is dropped (the local SQL container, LocalDB, a SQL Server here); one on another server - a shared or staging SQL Server the site's `web.config` points at - is kept, and the confirmation says so. A site whose folder is elsewhere (IIS's *Default Web Site*…) keeps its files; the database its web.config names is dropped. If files in the folder are still in use, it finds the programs holding them (open files, or a terminal / editor whose working folder is inside), lists them and - after you confirm - closes them and deletes the folder. Windows itself, services and Explorer are never closed; anything still locked is deleted at the next Windows restart. |

## Backups

All project backups are kept in one place, apart from the sites:
`Documents\DnnManager\backups\`, one folder per project and one dated folder per
backup:

```
Documents\DnnManager\backups\
├── ceesboer\
│   ├── ceesboer_20260928_154210\
│   │   ├── ceesboer.zip              ← the site files
│   │   └── ceesboer.bacpac           ← the database
│   └── ceesboer_20260930_091500\
│       └── ceesboer.bacpac           ← a database-only backup
└── settings.v0.20260929-101500.json  ← a settings.json copy (see Configuration)
```

Because they're outside the site, IIS never serves them, a site export never
includes them, and **removing a project keeps its backups**.

- **Projects → right-click → Export** writes a new dated folder (site, database
  or both); **Open backups folder** opens the project's folder. The site `.zip`
  leaves out `.git` and everything the site's `_backup.filter` lists (below).
- **Clone** keeps the source database backup it restored in a dated folder too.
- **New project → An existing site** lists every project with a complete backup
  (site + database) - including removed projects - and its backups by date. Pick
  one, or choose files anywhere on the PC.
- **Host project** offers the `.bacpac` / `.bak` files in the project's backups
  (and the project folder's root) to restore.

### `_backup.filter`

A `_backup.filter` in the project folder lists what the site `.zip` leaves out -
caches, search indexes, logs, big data files. It's the format Azure App Service
backups use, so the file a site already has for Azure works as it is: one path
per line, from `D:\home` (`\site\wwwroot\...` is the site root). A path without
that prefix counts from the site root, and a folder leaves out everything in it:

```text
\site\wwwroot\App_Data\Search
\site\wwwroot\App_Data\51Degrees.dat
\site\wwwroot\imagecache
Portals\_default\Logs
```

The activity log lists what was left out. `_backup.filter` itself is kept in the
zip, so a site imported from it has the same filter.

Backups made by DNN Manager 1.1 stay in each project's `01_backup` folder -
DNN Manager no longer uses it. Move the dated folders you want to keep to
`Documents\DnnManager\backups\<project>\`, then delete `01_backup` (otherwise it
is now included in the project's site `.zip`).

## Automatic DNN setup

**New project → Start from: a new site** with *Automatic setup* (the default)
installs DNN the way DNN's own unattended install does - not by clicking
through its wizard -, so the first visit shows the new site
([`SetupProjectUseCase`](../src/DnnManager.Application/UseCases/SetupProjectUseCase.cs),
[`DnnInstaller`](../src/DnnManager.Infrastructure/Dnn/DnnInstaller.cs)). What can
be checked is checked before anything is created: the project's name and
folder (a site path longer than 100 characters is refused - DNN's packages fail
to install below it), the host account (a password of 7 to 128 characters,
without `<` or `&#`: DNN finishes "successfully" without a host for a shorter
one, and its login form refuses those characters), IIS, and the database -
reachable, signed in, a SQL Server version the release supports, and allowed to
create the database (or owning it, empty, when it exists).

The **Output** tab follows it step by step:

1. **Testing database connection** - each check (the server answers, the
   sign-in works, its version, the permissions).
2. **Creating project directory**, **Downloading DNN** - the release is
   extracted into the folder.
3. **Creating IIS application pool and website** - bound to the host name and
   port you chose. For a LocalDB file the app pool loads its user profile
   (LocalDB needs it).
4. **Creating database** - with Windows authentication the site's app pool
   identity (`IIS APPPOOL\<project>`) gets a login and owns the database.
5. **Configuring DNN** - `web.config`'s `SiteSqlServer` points at the database,
   and `Install\DotNetNuke.install.config` is written from the package's own
   template: the host account, website name, language, site template and the
   portal alias (`<host name>[:port]`).
6. **Running DNN installation** - DNN's `Install/Install.aspx?mode=install`,
   requested by DNN Manager on this machine (with the site's host name, so no
   DNS or hosts entry is needed), its progress shown as it comes: the database
   scripts, each package (*installing … (12 of 47)*), **Creating portal**. DNN
   answers with HTTP 200 even when something failed, so every line is read: an
   *Error!*, a package that failed or a portal that wasn't created fails the
   setup.
7. **Creating host account** - checks what DNN made (its version against the
   files, the host superuser, the portal alias) and finishes what DNN's wizard
   would: the host isn't asked to change its password at its first sign-in,
   and the pages aren't marked secure (the local site is http).
8. **Starting website** - the home page is requested once, so DNN finishes its
   start-up work now and not on your first visit; anything DNN logged as an
   error meanwhile is shown.

*DNN installation completed. Open http://… - sign in as '…'.* ends it, with a
notification and **Open site**. The install template (it holds the host
password), DNN's `Install.aspx`, `InstallWizard.aspx` and `UpgradeWizard.aspx`
and the `web.config` backups DNN made are then deleted - also when the setup
fails or is cancelled -, so nothing in the folder installs DNN again or holds
the password. The password is never in the Output tab, the log file or
`settings.json`. DNN Manager notes how the project was installed (and its host
username) in `Documents\DnnManager\projects\<project>.json`, which the
overview's **DNN** tab shows.

If DNN's installation fails, the project is left as it is to look into: remove
it (**Remove…**) and create it again - DNN can't install twice into the same
files and database.

**Manual DNN setup** creates the folder, the IIS website and the database as
before and leaves DNN's installation wizard for the first visit; the Output tab
says what to enter in it.

### Databases

The connection type is chosen once, in **Settings → Database server**; New project
names the database like the project and tests the connection before it creates
anything.

| Type | What the site uses |
|---|---|
| **Local SQL container (Docker)** | A database named like the project on the shared container, as `sa` - the default. |
| **SQL Server / SQL Server Express** | Any SQL Server, e.g. `.\SQLEXPRESS`: with *Windows authentication* the site signs in as its app pool identity, which DNN Manager makes a login and the database's owner (on this machine's SQL Server); with *SQL Server authentication* as a login that may create the database, or owns it. |
| **SQL Server Express LocalDB (file)** | The package's own `App_Data\Database.mdf`, run by LocalDB under the site's app pool identity. Fine for trying things out; DNN Manager can't open it while the site runs. |

**Create project** first tests the database and shows each check in the Output panel: the server answers, the
sign-in works, the version is new enough (SQL Server 2017 for DNN 10, 2012 for
DNN 9), and the database can be created - or, when it exists, is empty and
owned (`db_owner`) by the login; with Windows authentication, that the site's
login can be made. A database that already exists is only dropped after you
confirm it.

### Changing the host password

**Change host password…** on the overview's **DNN** tab sets a new password for
a host account (pick it, type the new one twice, or **Generate**), hashed the
way the site's membership provider stores it - DNN keeps only that hash, so the
current password can't be shown -, then restarts the site (a LocalDB file's
site is stopped while its database is changed). The new password isn't kept
anywhere.

## Import a site .zip

**New project → Start from: an existing site** creates a project from a zipped
DNN site and its database backup - both required
([`ImportProjectUseCase`](../src/DnnManager.Application/UseCases/ImportProjectUseCase.cs)).
**From a project backup** picks a project and one of its dated backups in
`Documents\DnnManager\backups`; the **Browse…** buttons take a `.zip` and `.bacpac` from anywhere:

1. **Extract** the zip into a new folder `<BaseDirectory>\<name>`. The site root
   is the zip's shallowest folder with a `web.config`, so a zip with everything
   under one top folder works too; files outside it are skipped. Entries that
   would land outside the folder are refused, and a failed or cancelled
   extraction removes the folder again. The zip isn't searched for a database.
2. **Host it** exactly like [Host a project](#host-a-project) with *IIS website +
   database*: IIS site, HTTPS redirects switched off, the `.bacpac` (a `.bak`
   works too) restored,
   `dbo.PortalAlias` pointed at the local hostname, DNN's SSL switched off in the
   database and `web.config` pointed at the database (after asking).

## Host a project

For a DNN site whose files are **already** in a folder under `BaseDirectory`
(copied over by hand, checked out from git, left behind by an earlier run),
**Host project** creates only what is missing - the files are never
downloaded, copied or overwritten.

Flow ([`ExistingFolderPage`](../src/DnnManager.Presentation/Pages/ExistingFolderPage.xaml.cs)
→ [`HostExistingProjectUseCase`](../src/DnnManager.Application/UseCases/HostExistingProjectUseCase.cs)):

1. **Pick the folder** - each one shows whether it already has an IIS site.
2. **Choose** *IIS website + database* (default), *database only*
   or *IIS website only*.
3. **Pick a backup** (when the database is included) - a `.bacpac` or `.bak`
   found in the project's `backups\` folder or its root (newest first), any file
   picked with **Browse…**, or none. Copies of files such as `web.config.bak`
   are not database backups and aren't offered.
4. **IIS website** (unless database only) - checks the IIS features, then creates
   (or recreates) the site and app pool bound to `<folder>.<HostnameSuffix>`,
   grants the IIS identities access to the folder and starts the site.
   A production `web.config` often has a URL Rewrite rule that redirects every
   request to `https://`. The local site is HTTP-only, so such rules are
   switched off (`enabled="false"`, with a *Disabled by DNN Manager* comment
   above them) and the Output tab shows a **⚠ warning** to switch them back on
   before the site is deployed to production.
5. **Database** (unless IIS only) - checks the SQL Server connection. The
   database is the one `web.config` already uses on the local container, or
   otherwise one named like the folder. Then:
   - **with a backup**, restores it (`.bacpac` via SqlPackage, `.bak` via
     `RESTORE`) - asking first if the database already exists - and points
     `dbo.PortalAlias` at the local hostname so the site answers there. DNN's
     SSL (`SSLSetup`, or `SSLEnabled` / `SSLEnforced`, and pages marked secure)
     is switched off in the local database - the local site has no https, and
     DNN would otherwise redirect every request to `https://`;
   - **without one**, keeps an existing database as it is, or creates it empty
     (run the install wizard, or restore later by running **Host project**
     again with *database only* and a backup).

   Unless `web.config` already uses the local container, it then asks before
   pointing `web.config`'s `SiteSqlServer` at the database.

With the website, a database problem (e.g. SQL Server not reachable) is reported and
skipped; with *database only* it fails the run, since the database is the whole
job.

## Clone a project

**Clone…** on a project's right-click menu copies that DNN site (files +
database) into a new project under `BaseDirectory` with its own hostname, IIS
site and local database.

Flow ([`ProjectMenu`](../src/DnnManager.Presentation/Pages/Projects/ProjectMenu.cs)
→ [`CloneProjectUseCase`](../src/DnnManager.Application/UseCases/CloneProjectUseCase.cs)):

1. **Name** - the new project's name (`<project>_copy` is suggested); one that
   isn't valid or is taken is refused as you type.
2. **Source database** - always the `SiteSqlServer` connection in the source's
   `web.config`.
3. **Copy the website files** into the project folder.
4. **Check the local SQL Server** - log in as `sa` at `ContainerIp,DefaultPort`.
   If it doesn't answer, the database steps are skipped and the files are kept.
5. **Create the local database** in the shared SQL container (dropping it first
   if it exists) and seed it from the source:
   - an **Azure SQL** source is exported to a `.bacpac` (SqlPackage) and imported;
   - a source on the **local container** is backed up inside the container;
   - any **other SQL Server** is backed up with `BACKUP DATABASE`
     (`Microsoft.Data.SqlClient`) and restored with `RESTORE`.

   A copy of the backup is kept in the project's `backups\` folder.
6. **Rewrite `dbo.PortalAlias`** so portal 0's primary alias becomes the new
   hostname (see *Notes on cloning*), and switch DNN's SSL off in the copy, as
   Host project does.
7. **Point `web.config`** at the local database (supports the
   `configSource="..."` pattern; the external file is what gets rewritten). The
   site connects as the container `sa`.
8. **Create the IIS site** bound to `<name>.<HostnameSuffix>`.

### Notes on cloning

- DNN's user-facing **"Connection To The Database Failed"** page is shown for
  *any* startup exception, not just DB connection issues. When investigating,
  always read the actual exception from
  `Portals\_default\Logs\<date>.log.resources` inside the project.
- The portal-alias step
  ([`ISqlServerService.RemapPortalAliasesAsync`](../src/DnnManager.Application/Abstractions/Interfaces.cs))
  rewrites the first `*.<HostnameSuffix>` alias for `PortalID = 0` into the new
  hostname, inserts one if none matched, and removes leftover stale aliases.
  Without this step the cloned site throws
  `NullReferenceException at PortalSettingsController.ConfigureActiveTab`
  because no alias matches the incoming request.

## Keep warm

A local DNN site is slow after a while without visits because IIS shuts its app
pool's worker process down after its **idle time-out** - 20 minutes by default.
The next request starts a new one, and ASP.NET and DNN start up again: 3 to 10
seconds instead of some 30 ms - and the first page after that takes almost a
second more. Recycles (every 29 hours by default, and DNN Manager's own: Restart,
Clear website cache…) and IIS starting do the same.

Click the **flame** in a site's row - or **Keep warm** on its right-click menu,
or the switch on its overview's **IIS** tab - and DNN Manager keeps the site warm
while it runs, minimized too:

- **Before IIS would shut it down**, it requests the site's keep-alive page -
  DNN's own `KeepAlive.aspx`, about 100 bytes, answered in a few milliseconds
  without database work - every 5 minutes (**Settings → Projects → Keep warm**),
  or sooner when the app pool's idle time-out needs it (at most 40% of it, but
  not more often than every 30 seconds). Not while the site is in use anyway -
  IIS's request counter went up since the last look - unless the idle time-out
  is so short (about a minute) that two intervals wouldn't fit in it. Also when
  the app pool never idles out: DNN itself still restarts after a rebuild, and
  that request starts it again.
- **As soon as its worker process is gone** - a recycle, a crash, the site or IIS
  started again - it warms the site up with its warm-up page, the home page (`/`):
  DNN starts and the page is compiled before you open it. A keep-alive request
  that comes back slowly (DNN had to start, after a rebuild say) gets a warm-up
  too. A site without a keep-alive page (it answers *404* twice in a row) gets
  its warm-up page instead.
- **No browser**, no process of its own: a plain request straight to the site on
  this PC (`127.0.0.1`, with the site's host name - whatever DNS says), as one
  anonymous visitor that keeps its cookies. Redirects are only followed to the
  same site, and never into DNN's installer.

It holds back while the site or IIS is stopped, while one of DNN Manager's
operations runs on the site (or on IIS itself), while a debugger is attached to
the site's worker process - Visual Studio's usual attach for .NET code too: a
request would stop at your breakpoints - and, for a warm-up, while the SQL
Server container the site's database is on doesn't answer. During other
operations (a new project, an import, a backup…) running sites are kept warm,
but none is warmed up from cold until the operation has ended; and nothing an
operation may have caused counts as a failure. Right after DNN Manager starts
it waits a minute before warming up sites without a worker process, and it warms
up one site at a time. A site that keeps failing is tried again after 1, 2 and 5
minutes, then left alone until you press **Check now**, start it again or the PC
wakes up - and so is one whose worker process keeps ending right after it was
warmed up (a crashing site). A keep-alive request that gets no answer in time is
followed by a warm-up, with its longer time limit (DNN may be starting slowly
after a rebuild) - or, while a debugger is attached, taken to be stopped at a
breakpoint: not a failure, and the next request only comes an interval later.
The first failure, giving up and recovering are noted in the log file; the
**Output** tab's *Background* list shows each site's state.

The flame shows how it is going: an outline while off; filled in orange while
the site is warm, pulsing while it warms up, grey while paused, with a red dot
while it fails. Its tooltip only says what a click does - *Enable keep warm* or
*Disable keep warm*. The overview's **Keep warm** card says how it is going and
shows the app pool's idle time-out, how often the site is requested and which
pages - the interval and pages from **Settings → Projects → Keep warm**.

Switching it on asks nothing and needs nothing of the site - no mail server, no
extension, no change in IIS or DNN: plain HTTP requests, which any DNN answers.
Kept warm, the site's DNN keeps running, and so does its scheduler (with
whatever it sends, e-mail included, when the site has a mail server set up).

Which sites are kept warm is remembered in `Documents\DnnManager\projects\keep-warm`;
a site's file goes when the site is no longer in IIS (removed with **Remove…**,
or in IIS Manager - also while DNN Manager was closed). Keep warm changes
nothing in IIS: when DNN Manager is closed, the sites idle out as IIS has them
set up.

## Notes / limitations

- The dark title bar needs Windows 10 20H1 or later; older versions keep a light one.
- File pickers (Browse… / Save as) are standard Windows dialogs and follow the
  Windows theme, not the app's. Questions and warnings use the app's own dialog.
- The app runs as Administrator, so an IDE opened from the project menu does too
  (VS Code shows *[Administrator]* in its title).
- The SQL Server keeps the sa password its data volume was created with -
  changing **SA password** in Settings doesn't change it in an existing
  container.
- Automatic DNN setup is tested with DNN 10.3.3 on IIS Express, with LocalDB and
  a SQL Server 2022 container (DNN's unattended install itself also with DNN
  9.13.10). On IIS the site signs in to SQL Server as `IIS APPPOOL\<project>`,
  which only works for a SQL Server on this machine - for one on another
  machine, use SQL Server authentication. A language other than English
  (`en-US`) makes DNN download its language pack while installing.
- [Keep warm](#keep-warm) only works while DNN Manager runs. A site that
  restarts without a new worker process - a rebuild changed `bin` or
  `web.config` - is warmed by its next keep-alive request, within the interval.
- Cloning from a SQL Server on another machine (not Azure SQL) writes the
  `.bak` on that server, in this PC's temp path, so it only works when the source
  server runs on this machine. Azure SQL sources go through a `.bacpac` and work
  from anywhere.
