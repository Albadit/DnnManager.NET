# Releasing

Making a new version: the steps, the portable exe and the installer.

## Steps

1. Set `<Version>` in [`DnnManager.csproj`](../DnnManager.csproj) - the exe, the
   portable exe's name, the installer and Settings → About all take it from there.
2. In [`CHANGELOG.md`](../CHANGELOG.md), turn **Unreleased** into
   `## vX.Y.Z - <date>` (with an *Upgrading* note when settings or behaviour change).
3. Run the fast tests and the integration tests ([testing.md](testing.md)).
4. [Publish the portable exe](#publish-the-portable-exe) and
   [build the installer](#build-the-installer).
5. Tag `vX.Y.Z` and make a GitHub release from the changelog entry, with
   `DnnManager-X.Y.Z-x64.exe` and `DnnManagerSetup-X.Y.Z-x64.exe` attached -
   Settings → About compares the running version with the newest release there.

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
`-SkipPublish` reuses the last publish; `-Iscc <path>` picks the compiler.

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

VS Code tasks for build, publish and the installer are in `.vscode/tasks.json`.
