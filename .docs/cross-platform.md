# DNN Manager on macOS, Linux and Windows

**From 2.0, DNN Manager is one app on all three systems:** [`src/DnnManager.Desktop`](../src/DnnManager.Desktop/DnnManager.Desktop.csproj)
([Avalonia](https://avaloniaui.net), MIT licensed), with every page and the look of the WPF app it replaces
(`DnnManager.csproj`, no longer released; its theme is the source of the Avalonia one - `.github/scripts/port-theme.py`).
On Windows, Setup and the portable exe install it under the old names (`DnnManager.exe`, `DnnManager-<version>-x64.exe`).

| | Windows | macOS and Linux |
|---|---|---|
| Makes and runs DNN sites (Projects, New project: IIS, LocalDB, the hosts file) | Yes | No - see [Why not IIS](#why-not-iis-on-macos-and-linux) |
| Source Control, Settings, Troubleshoot, the command palette, the bottom panel | Yes | Yes |
| Administrator rights | Asks for them at start (IIS needs them); `DNNMANAGER_NO_ELEVATION=1` skips it | Never needs them |
| Updates itself (the **Update** button) | Yes | Says a newer version is out, with the download |

## Install

Download the file for your system from the [GitHub release](https://github.com/Albadit/DnnManager.NET/releases),
next to Windows' Setup and portable exe. On macOS and Linux it is **the program itself**, one file, no zip - nothing
to unpack, no .NET to install. The release workflow makes them with the **Desktop** workflow
([`.github/workflows/desktop.yml`](../.github/workflows/desktop.yml)), which also runs by hand
(*Actions → Desktop → Run workflow*), with the files as the run's artifacts.

| System | File |
|---|---|
| macOS, Apple Silicon (M1-M4) | `DnnManager-<version>-macos-arm64` |
| macOS, Intel | `DnnManager-<version>-macos-x64` |
| Linux, x64 | `DnnManager-<version>-linux-x64` |
| Linux, arm64 | `DnnManager-<version>-linux-arm64` |

**The first time**, in Terminal, in the folder you downloaded it to. A download loses the file's "can run"
permission, and macOS marks it as downloaded from the internet:

```sh
chmod +x DnnManager-2.0.0-macos-arm64                          # macOS and Linux: allow it to run
xattr -d com.apple.quarantine DnnManager-2.0.0-macos-arm64     # macOS only
./DnnManager-2.0.0-macos-arm64
```

Nothing is paid for, so nothing is signed with a bought certificate. The macOS file is signed *ad hoc* (which
Apple Silicon needs to start it, and which costs nothing) but not notarized: without the `xattr` line, macOS says it
can't check the developer - then **System Settings → Privacy & Security → Open Anyway**.

For a Mac app in Applications (`DNN Manager.app`) or a Linux menu entry, build the bundles on that system with the
VS Code task **Publish → Setup** (`package.ps1 -Kind bundle`).
On **Windows**, the release's Setup (`DnnManager_Setup-<version>-x64.exe`) or portable exe
(`DnnManager-<version>-x64.exe`, `-x86.exe` for 32-bit) is this same app. Windows SmartScreen may ask first
(*More info → Run anyway*).

Each is one self-contained program: no .NET to install.

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

- **From the WPF app:** the bottom panel's Terminal tab (Ctrl+` shows the panel), the site, IIS and Windows logs in
  its Logs tab (DNN Manager's own log is there), Settings' **Edit upgrade path…**, and keeping unsaved form drafts
  across a restart. Logs of a project open in a window of their own.
- **Icons on macOS and Linux:** the Windows app's icons are a Windows font (Segoe Fluent Icons) that can't be
  shipped - drawn icons or text stand in there.
- **Tried on a real Mac and Linux PC:** CI builds and tests it on macOS and Linux,
  and its windows are tested drawn headless; it has been run by hand on Windows
  only so far.
