# Releasing

Making a new version: the steps, the release workflow, the portable exe and the installer.

## Steps

1. Set `<Version>` in [`DnnManager.csproj`](../DnnManager.csproj) - local builds,
   the portable exe's name, the installer and Settings → About take it from there.
2. In [`CHANGELOG.md`](../CHANGELOG.md), turn **Unreleased** into
   `## vX.Y.Z - <date>` (with an *Upgrading* note when settings or behaviour change).
3. Run the fast tests and the integration tests ([testing.md](testing.md)).
4. Commit, push, then tag and push the tag:

   ```bash
   git tag v1.7.0
   git push origin v1.7.0
   ```

   The [release workflow](#the-release-workflow) builds, tests and publishes the
   GitHub release with `DnnManager-X.Y.Z-x64.exe`, `DnnManagerSetup-X.Y.Z-x64.exe`
   and `SHA256SUMS.txt` attached - Settings → About compares the running version
   with the newest release there.

## The release workflow

[`.github/workflows/release.yml`](../.github/workflows/release.yml) runs on every
pushed tag `vX.Y.Z` (or `vX.Y.Z-suffix`, which becomes a pre-release):

1. Restores, builds in Release and runs the whole test suite - a failing build or
   test stops it before anything is published. The integration tests are skipped
   (inconclusive) where the runner lacks IIS Express, LocalDB or Linux containers.
2. Takes the version from the tag and stamps it into the exe (`Version`,
   `AssemblyVersion`, `FileVersion`, `app.manifest`) and the installer, whatever
   `<Version>` says - it warns when the two differ.
3. Publishes the portable exe, builds the installer and checks that both report
   the tag's version.
4. Writes the release notes with
   [`.github/scripts/release-notes.ps1`](../.github/scripts/release-notes.ps1): the
   tag's `CHANGELOG.md` entry sorted into *What's new*, *Improvements*, *Bug fixes*,
   *Security*, *Removed* and *Installation / Update notes*, followed by the commits
   since the previous tag. Without a changelog entry the sections are made from
   the commit subjects (`new:`, `fix:`, `update:`…).
5. Creates the GitHub release for the tag (or updates it when re-run) and uploads
   the files. They are also kept as a workflow artifact for 30 days.

**Actions → Release → Run workflow** with a version is a dry run: everything
except publishing the release. Preview the notes locally with
`.github\scripts\release-notes.ps1 -Version 1.7.0 -OutFile notes.md`.

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
comes from `<Version>` in `DnnManager.csproj`. It uses an installed Inno Setup 6
when there is one, otherwise it downloads a pinned copy (the `Tools.InnoSetup`
package from nuget.org) into `src\DnnManager.Installer\bin\tools` - no admin
rights needed. Everything made along the way (the published app, wizard images,
Inno Setup) is in `src\DnnManager.Installer\bin`; the finished Setup is in
`publish\`.
`-SkipPublish` reuses the last publish; `-Iscc <path>` picks the compiler;
`-Version 1.7.0` builds that version instead of `<Version>` (the release workflow
passes the tag's).

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

VS Code tasks for build, publish and the installer are in `.vscode/tasks.json`.
