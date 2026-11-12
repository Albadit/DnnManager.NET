# DNN Manager on macOS, Linux and Windows

DNN Manager comes as two apps built on the same code:

| | The Windows app | DNN Manager for macOS, Linux and Windows |
|---|---|---|
| Project | [`DnnManager.csproj`](../DnnManager.csproj) (WPF) | [`src/DnnManager.Desktop`](../src/DnnManager.Desktop/DnnManager.Desktop.csproj) ([Avalonia](https://avaloniaui.net), MIT licensed) |
| Runs on | Windows 10 / 11, Windows Server | macOS 12+ (Apple Silicon and Intel), Linux (x64, arm64), Windows |
| Makes and runs DNN sites (IIS, LocalDB, the hosts file) | Yes | No - see [Why not IIS](#why-not-iis-on-macos-and-linux) |
| Source Control (compare, push, pull, history, rollback over FTP / SFTP) | Yes | Yes |
| Administrator rights | Asks for them (IIS needs them) | Never needs them |

The cross-platform app is new and has **Source Control** and **Settings** so far; the
other pages say what they need. Its pages are being moved over from the Windows
app one at a time - until they all are, the Windows app stays the one for making
and running sites.

## Install

Download the package for your system from the [GitHub release](https://github.com/Albadit/DnnManager.NET/releases),
next to the Windows app's Setup and portable exe. They are on releases from
2.0.0-rc1 on, a pre-release. The release workflow makes them with the **Desktop** workflow
([`.github/workflows/desktop.yml`](../.github/workflows/desktop.yml)). That workflow also runs by hand
(*Actions → Desktop → Run workflow*), with the packages as the run's artifacts.

Nothing is paid for, so nothing is signed with a bought certificate:

- **macOS** - `DnnManager-<version>-macos-arm64.zip` (Apple Silicon) or `-macos-x64.zip`
  (Intel). Unzip it and move **DNN Manager.app** to Applications. The app is signed
  *ad hoc* (which Apple Silicon needs to start it, and which costs nothing) but not
  notarized, so the first time macOS says it can't check the developer: **right-click
  the app → Open → Open**. From then on it starts as any app. (Or, in a terminal:
  `xattr -dr com.apple.quarantine "/Applications/DNN Manager.app"`.)
- **Linux** - `DnnManager-<version>-linux-x64.tar.gz` (or `-linux-arm64`). Unpack it
  anywhere and run `./DnnManager.Desktop`. To have it in the menu, copy
  `dnn-manager.desktop` to `~/.local/share/applications/` and set its `Exec=` to
  the program's full path.
- **Windows** - the release's Setup or portable exe: the Windows app, with IIS. The
  cross-platform app for Windows, `DnnManager-<version>-windows-x64.zip`, comes only
  from a run of the Desktop workflow by hand: unzip it and run
  `DnnManager.Desktop.exe`. Windows SmartScreen may ask first (*More info → Run anyway*).

Each package is one self-contained program: no .NET to install.

## Where your data is

The same place and the same database as the Windows app's:
`Documents/DnnManager/dnnmanager.db` - your servers, their history, the settings.
`DNNMANAGER_DATA_DIR` set to a full path puts it elsewhere (a portable copy, or a
try-out apart from your real data).

**Saved passwords and keys** are encrypted so only you, on this computer, can read
them back - a copy of the database alone gives nothing away:

- **Windows:** with DPAPI, as the Windows app always has.
- **macOS and Linux:** with AES-256-GCM and a random key of your own, in a file only
  you can read (mode 600), apart from the database:
  `~/Library/Application Support/DnnManager/user.key` on macOS,
  `~/.config/DnnManager/user.key` on Linux (`$XDG_CONFIG_HOME` when it is set).
  Lose that file and the saved passwords can't be read - add them again. Settings →
  About shows where it is.

Like DPAPI, this protects against the database being copied, not against a program
that runs as you.

**The projects folder** starts as `C:\DNN` on Windows and `~/DNN` on macOS and
Linux (Settings → Projects folder). Each folder in it is a project Source Control
lists; a server can also have a **local folder** of its own, anywhere - the
project's files don't have to be in the projects folder.

## Why not IIS on macOS and Linux

DNN Platform - 9 and 10 alike - runs on **.NET Framework 4.8 inside IIS**, and
both exist only on Windows. There is no IIS for macOS or Linux, Docker there runs
Linux containers only (a Windows/IIS container won't start), and SQL Server
Express LocalDB is Windows only. So on macOS and Linux, DNN Manager doesn't offer to
make or run sites - the pages say so, and why.

To work on DNN sites on a Mac or Linux PC:

- **Make and run them in a Windows virtual machine** (Parallels, UTM or VMware
  Fusion on a Mac; VirtualBox, VMware or KVM on Linux - Windows 11 on ARM runs
  IIS on Apple Silicon) with the Windows app in it.
- **Edit the files on the Mac** (any editor) and **push and pull them** with
  Source Control here - to the VM's site (over SFTP: Windows' OpenSSH server) or
  to the real servers.

## Building it

```bash
dotnet run --project src/DnnManager.Desktop                       # build and start
dotnet test tests/DnnManager.Desktop.Tests                        # its tests - on any system
dotnet publish src/DnnManager.Desktop -c Release -r osx-arm64     # one self-contained program
```

In VS Code, **Ctrl+Shift+B → Publish** → **Portable** or **Setup** → tick **macOS**
and/or **Linux** builds them into `publish\` ([releasing.md](releasing.md#build-on-this-pc-the-publish-task)).

The runtimes it is published for are `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`,
`linux-x64` and `linux-arm64`. A macOS package is made on a Mac (it signs the app
with `codesign`): [`Packaging/package.ps1`](../src/DnnManager.Desktop/Packaging/package.ps1).

How the code is split between the two apps: [architecture.md](architecture.md#layers),
and why: [ADR 0006](adr/0006-cross-platform-avalonia.md).

## What isn't there yet

- **Pages:** Projects, New project, Troubleshoot and most of Settings are only in the
  Windows app (Projects and New project need IIS anyway). In Source Control: the
  Explorer and Search views, the editor's tabs, the three-way merge of a conflict
  (*Keep the project's* and *Take the server's* are there), restoring one file from
  the history, and the command palette.
- **Updates:** the cross-platform app doesn't update itself yet - download the new
  package.
- **Tried on a real Mac and Linux PC:** CI builds and tests it on macOS and Linux,
  and its windows are tested drawn headless; it has been run by hand on Windows
  only so far.
