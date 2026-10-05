# Releasing

Making a new version: the steps, the release workflow, the portable exe and the installer.

## Steps

There is no version number to raise: the release's tag is its version. A release
build gets it from the tag (`v1.7.2` → 1.7.2), and every other build from the
newest version tag in git that the commit includes - after 1.7.1 is released, a
local build, the portable exe's name, the installer and Settings → About say 1.7.1
until 1.7.2 is tagged ([`DnnManager.csproj`](../DnnManager.csproj), target
`VersionFromGitTag`; without git or a tag it is 0.0.0, with a build warning).

1. In [`CHANGELOG.md`](../CHANGELOG.md), turn **Unreleased** into
   `## vX.Y.Z` (with an *Upgrading* note when settings or behaviour change).
2. Run the fast tests and the integration tests ([testing.md](testing.md)).
3. Write the release notes as `docs/release-notes/vX.Y.Z.md` - the file's name is
   the release's tag and title. Every release uses the structure of
   [v1.6.0](release-notes/v1.6.0.md): a bold summary, *Highlights*, *Other changes*,
   *Upgrading*, *Tested*. With Claude Code, ask for "the release notes for X.Y.Z" -
   the `release-notes` skill (`.claude/skills/release-notes`) writes them from the
   changelog, the commits and the test results. By hand, start from the draft:
   `.github\scripts\release-notes.ps1 -Version X.Y.Z -OutFile docs\release-notes\vX.Y.Z.md`.
   The notes are built into the exe (What's new after an update shows them), so
   they must be committed before the release is built.
4. Commit and push, then publish the release - either way the GitHub release gets
   `DnnManager-X.Y.Z-x64.exe` and `DnnManagerSetup-X.Y.Z-x64.exe`, and every running DNN Manager
   offers it with its **Update** button (see [The in-app update](#the-in-app-update)):
   - **From VS Code**: run the task **release (GitHub)** - see
     [Release from VS Code](#release-from-vs-code).
   - **From GitHub Actions**: tag the commit and push the tag; the
     [release workflow](#the-release-workflow) does the rest.

     ```bash
     git tag v1.7.0
     git push origin v1.7.0
     ```

## Release from VS Code

**Ctrl+Shift+B → release (GitHub)** (or **Terminal → Run Task…**) asks two
questions in VS Code's picker, then runs
[`.github/scripts/publish-release.ps1`](../.github/scripts/publish-release.ps1) in
the terminal. The pickers are filled fresh each time - from `docs/release-notes`
and from GitHub - by the extension
[Tasks Shell Input](https://marketplace.visualstudio.com/items?itemName=augustocdias.tasks-shell-input)
(`augustocdias.tasks-shell-input`); VS Code offers to install it, as it's in
`.vscode/extensions.json`.

1. **Pick the release notes** from `docs/release-notes`. Files without a tag come
   first, marked *next release*; released ones are marked *already released*.
   `v1.7.0.md` makes the tag and the release title `v1.7.0` (`v1.7.0-rc.1.md`
   makes a pre-release).
2. **Pick the commit** from the 20 newest on GitHub (`origin/<current branch>`,
   fetched first) - the newest is on top.
3. It stops when the tag already points at another commit or GitHub already has
   that release.
4. In a temporary worktree of that commit - your working copy isn't touched - it
   runs the fast tests, then builds the portable exe and the installer with the
   version stamped in, the same build as the release workflow.
5. It checks both files report the version; they land in `publish\vX.Y.Z\`.
6. **After you confirm**, it tags the commit, pushes the tag, creates the release as
   a draft with the notes, uploads the two files, checks their sizes and
   publishes it. Answer *N* and nothing is published - the files stay in
   `publish\vX.Y.Z\`.

It signs in to GitHub with the credential Git uses for this repository. Outside VS
Code, `.github\scripts\publish-release.ps1` asks the same two questions in the
terminal; `-NotesFile docs\release-notes\v1.7.0.md -Commit <hash>` answers them and
`-SkipTests` skips the fast tests. A commit that isn't on GitHub yet is released
only after you confirm. If the release workflow runs for the pushed tag too, it
leaves the published release as it is.

## Redo a release

When a release failed (the workflow's tests, say) or needs one more change after
its tag is made, **Ctrl+Shift+B → release: redo (GitHub)** runs
[`.github/scripts/redo-release.ps1`](../.github/scripts/redo-release.ps1):

1. It asks for the version (Enter takes the newest tag) and a new message for the
   release commit (Enter keeps it).
2. The release commit is the tag's commit, and it must be the newest commit of
   the branch - one with commits after it is refused. Without a tag, the newest
   commit counts when its message starts with `release: vX.Y.Z`.
3. It shows the plan and asks once: amend the release commit with every change in
   your working copy (new files too), push the branch (`--force-with-lease` when
   the old commit is on GitHub - refused when GitHub's branch has commits yours
   hasn't), delete the tag's GitHub release (a published one only after you type
   the tag) and delete the tag here and on GitHub.
4. Then it releases the version again: build and publish from this PC (as
   **release (GitHub)**), push the tag for the [release workflow](#the-release-workflow),
   or neither yet.

`-DryRun` shows the plan and changes nothing; `-Version 1.7.6 -Message "release: …"`
answers the questions and `-SkipTests` is passed on to the build.

**Redo only a release nobody has installed yet.** DNN Manager updates only to a
newer version: whoever installed the first build of that version is never offered
the redone one. GitHub's release page shows the files' download counts; once others
may have it, release the change as the next version instead.

## The release workflow

[`.github/workflows/release.yml`](../.github/workflows/release.yml) runs on every
pushed tag `vX.Y.Z` (or `vX.Y.Z-suffix`, which becomes a pre-release):

1. Restores, builds in Release and runs the whole test suite - a failing build or
   test stops it before anything is published. The integration tests are skipped
   (inconclusive) where the runner lacks IIS Express, LocalDB or Linux containers.
2. Takes the version from the tag and stamps it into the exe (`Version`,
   `AssemblyVersion`, `FileVersion`, `app.manifest`) and the installer.
3. Publishes the portable exe, builds the installer and checks that both report
   the tag's version.
4. Writes the release notes with
   [`.github/scripts/release-notes.ps1`](../.github/scripts/release-notes.ps1):
   [`docs/release-notes/vX.Y.Z.md`](release-notes/) when it exists, with its relative
   links pointed at the tag's files;
   otherwise a draft in the same structure, from the tag's `CHANGELOG.md` entry
   (*Added* → *Highlights*, *Fixed* → `Fixed:` lines in *Other changes*, *Upgrading*,
   *Tested*) or, without an entry, from the commit subjects since the previous tag
   (`new:` → *Highlights*, `fix:` and `update:` → *Other changes*).
5. Creates the GitHub release for the tag and uploads the files - unless the tag
   already has a release (published from VS Code, or by an earlier run), which it
   leaves as it is. The files are also kept as a workflow artifact for 30 days.

**Actions → Release → Run workflow** with a version is a dry run: everything
except publishing the release. Preview the notes locally with
`.github\scripts\release-notes.ps1 -Version 1.7.0 -OutFile notes.md`.

## The in-app update

Every DNN Manager from 1.7.0 on updates itself from GitHub's **latest release** (never a
draft or a pre-release - a `vX.Y.Z-rc.1` tag isn't offered) - see
[Update](user-guide.md#update). A release must keep what it relies on:

- **The file names**: `DnnManagerSetup-<version>-x64.exe` updates an installed DNN
  Manager, `DnnManager-<version>-x64.exe` a portable one
  ([`AppRelease.AssetFor`](../src/DnnManager.Infrastructure/Updates/AppRelease.cs)).
- **The version inside both files**: their *ProductVersion* must be the tag's version
  (the workflow's check in step 3) - a download whose version differs is refused.
- **GitHub's SHA-256** of each file (the asset's `digest`): checked when GitHub lists it.
- **Silent Setup**: the update runs Setup with `/SILENT /SUPPRESSMSGBOXES /NORESTART
  /NOCANCEL /SP- /CURRENTUSER` (or `/ALLUSERS`) - `DnnManager.iss` must keep
  installing without questions that way, and keep its `AppId`.
- **Setup's hand-over**: a Setup newer than 1.7.1 asks GitHub for the latest release as
  it starts. A new install and **Update** install that one, **Repair** the installed
  version (the release `v<installed version>`); for any version but its own, Setup
  downloads that release's `DnnManagerSetup-<version>-x64.exe` (GitHub's
  SHA-256, and a *file version* of `<version>.0`), copies it to
  `%TEMP%\DnnManager-update\<version>\` and starts it with
  `/SP- /HandedOver=1 /CURRENTUSER` (or `/ALLUSERS`), then closes. A newer Setup must
  keep `/HandedOver=1` meaning: don't ask GitHub, skip the license and the
  Repair / Uninstall page, and use a Setup mutex of its own
  (`SetupMutex=DnnManager.NET.Setup{param:HandedOver|}{param:Done|}`) - the older one may not have
  exited yet. Setup's log (`/LOG=<file>`) records what GitHub answered.
- **Back to Setup's first page**: after Repair or Update (from 1.7.6 on) Setup skips its
  Finished page and starts itself again with `/SP- /Done=Repaired` (or `Updated`,
  or `Uninstalled` after the uninstaller), plus `/CURRENTUSER` or `/ALLUSERS`. A
  Setup it hands over to gets `/Back=Repaired` (or `Updated`) to do the same; an
  older one ignores it and shows its Finished page.
- **A running DNN Manager**: Setup has no `AppMutex`, so it opens while DNN Manager
  runs. Just before replacing or removing the files (`PrepareToInstall`, and the
  uninstaller's `usUninstall` step - after its confirmation) it sets the event
  `DnnManager.NET.Quit`, which DNN Manager 1.7.6 and newer listen to
  ([`SingleInstance`](../src/DnnManager.Presentation/SingleInstance.cs)) and quit on
  without asking (an open dialog is closed, a running operation cancelled, the
  workspace saved), and waits up to 10 seconds for the mutex `DnnManager.NET.Running`
  to go. Still there (an older version, or no answer): Setup doesn't run elevated,
  so it ends the process with an elevated PowerShell (`runas` - one UAC prompt) -
  only `DnnManager.exe` in the install folder, never the update helper in `%TEMP%` -
  and waits 5 more seconds. Only then does it ask the user to quit it and **Retry**.

How it works: the running DNN Manager downloads and checks the file, notes the
update (the `update` area of the `state` table in `Documents\DnnManager\dnnmanager.db` -
where the user is, the workspace saves as DNN Manager closes), copies its own exe to
`%TEMP%\DnnManager-update\<version>\helper-…\` and closes, starting that copy with
`--apply-update plan.json`. The copy ([`UpdateHelper`](../src/DnnManager.Infrastructure/Updates/UpdateHelper.cs))
waits for it to exit, runs Setup or swaps the portable exe (with a backup it puts
back if anything fails), and starts DNN Manager again; the new version restores the
workspace and checks it is the version the update meant to install.
The helper is the *old* version's code, so a release can change the helper only for
the updates after it.

**Try it** with the VS Code task **publish (update test, one version below the release)**
([`.github/scripts/build-update-test.ps1`](../.github/scripts/build-update-test.ps1)): it
builds the working copy as a portable exe one version below GitHub's newest release
(1.6.0 → 1.5.9, 1.7.0 → 1.6.9, 2.0.0 → 1.9.9) into
`publish\update-test\DnnManager-<version>-x64.exe`. Start it and click **Update**: it
installs the real release over itself. `-Version 1.5.9` picks the version by hand.

## Publish the portable exe

One file, no .NET runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PortableExe=true -o publish
```

`-p:PortableExe=true` names the exe `publish\DnnManager-<version>-x64.exe`
(without it, it's `DnnManager.exe`). The publish output holds only the program. Settings live in
`Documents\DnnManager` and are created on first start (see
[configuration.md](configuration.md)).

> **"Access to the path '...\publish\DnnManager.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

## Build the installer

```powershell
.\src\DnnManager.Installer\build.ps1
```

Publishes the app (self-contained, single file) into
`src\DnnManager.Installer\bin\app`, then compiles
[`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss) with Inno Setup
into `publish\DnnManagerSetup-<version>-x64.exe`. The version
is the newest version tag in git, as for every local build (see [Steps](#steps)). It uses an installed Inno Setup 6
when there is one, otherwise it downloads a pinned copy (the `Tools.InnoSetup`
package from nuget.org) into `src\DnnManager.Installer\bin\tools` - no admin
rights needed. Everything made along the way (the published app, wizard images,
Inno Setup) is in `src\DnnManager.Installer\bin`; the finished Setup is in
`publish\`.
`-SkipPublish` reuses the last publish; `-Iscc <path>` picks the compiler;
`-Version 1.7.0` builds that version instead of the tag's (the release workflow
passes the tag's).

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

VS Code tasks for build, publish and the installer are in `.vscode/tasks.json`.
