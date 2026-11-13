<#
.SYNOPSIS
    Builds DNN Manager on this PC, for the platforms you choose, as a portable program or a setup - into publish\.
    The VS Code task "Publish" asks for both.

.DESCRIPTION
    -Kind portable
      windows  publish\DnnManager-<version>-windows-x64\DnnManager-<version>-x64.exe and -windows-x86\...-x86.exe
               (32-bit) - the Windows app, one exe, nothing to install.
      macos    publish\DnnManager-<version>-macos-arm64\DnnManager.Desktop and -macos-x64 - the cross-platform app,
               one self-contained program each. Made on any system; on the Mac it needs chmod +x and an ad-hoc
               signature (codesign -s -) before it starts - see .docs\cross-platform.md.
      linux    publish\DnnManager-<version>-linux-x64\DnnManager.Desktop and -linux-arm64 (chmod +x on Linux).

    -Kind setup
      windows  publish\DnnManager-<version>-windows-x64\DnnManager_Setup-<version>-x64.exe - src\DnnManager.Installer\build.ps1;
               64-bit only (for 32-bit Windows, choose portable). The launcher needs
               Visual Studio's C++ build tools; without them Setup is built without it (as build.ps1 -NoLauncher:
               fine to try out, never for a release), and the script says so.
      macos    publish\DnnManager-<version>-macos-arm64.zip and -macos-x64.zip: DNN Manager.app, signed ad hoc. Only on
               a Mac - codesign, ditto and the icon tools are macOS's.
      linux    publish\DnnManager-<version>-linux-x64.tar.gz and -linux-arm64.tar.gz. On Linux or a Mac: the program
               keeps its "can run" permission only when it is packed there.
      What this system can't make is named, with where to make it instead (the Desktop workflow on GitHub builds
      every package: Actions -> Desktop -> Run workflow); the rest is still made.

    The version is the newest version tag in git, as for every local build (see .docs\releasing.md).

.EXAMPLE
    .github\scripts\publish-local.ps1 -Kind portable -Platforms windows,macos
.EXAMPLE
    .github\scripts\publish-local.ps1 -Kind setup -Platforms windows
.EXAMPLE
    .github\scripts\publish-local.ps1 -List platforms
#>
[CmdletBinding()]
param(
    [ValidateSet('portable', 'setup')]
    [string]$Kind,
    # windows, macos, linux - one or more ("windows,macos").
    [string[]]$Platforms,
    [string]$Configuration = 'Release',
    # Only prints the choices for VS Code's picker (Tasks Shell Input): value||label||description||detail.
    [ValidateSet('platforms')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($List -eq 'platforms') {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    'windows||$(device-desktop) Windows||64-bit and 32-bit||The Windows app, with IIS. Portable: x64 and x86. Setup: x64 only'
    'macos||$(device-desktop) macOS||Apple Silicon and Intel||Portable: the program. Setup: DNN Manager.app in a zip - only on a Mac'
    'linux||$(terminal-linux) Linux||x64 and arm64||Portable: the program. Setup: a tar.gz - only on Linux or a Mac'
    return
}

Import-Module (Join-Path $PSScriptRoot 'ReleaseCommon.psm1') -Force
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-ReleaseContext -Root $root
$out = Join-Path $root 'publish'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $Kind) { throw 'Choose -Kind portable or -Kind setup.' }
$Platforms = ConvertTo-ReleasePlatforms $Platforms
if (-not $Platforms) { throw 'Choose one or more platforms: -Platforms windows,macos,linux.' }

$onWindows = [IO.Path]::DirectorySeparatorChar -eq '\'
$onMac = -not $onWindows -and (Test-Path '/usr/bin/codesign')

# The version: the newest version tag the commit includes (2.0.0-rc1), as DnnManager.csproj takes it; 0.0.0 without one.
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$described = & git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
$ErrorActionPreference = $prev
$version = if ("$described" -match '^v(\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$') { $Matches[1] } else { '0.0.0' }
$fileVersion = "$($version -replace '-.*$', '').0"
Write-Host "DNN Manager $version - $Kind for $($Platforms -join ', ')" -ForegroundColor Cyan

$failed = $false
$made = [Collections.Generic.List[string]]::new()
$notMade = [Collections.Generic.List[string]]::new()
$runtimes = [ordered]@{
    windows = @(@{ Rid = 'win-x64'; Name = 'windows-x64'; Arch = 'x64' }, @{ Rid = 'win-x86'; Name = 'windows-x86'; Arch = 'x86' })
    macos = @(@{ Rid = 'osx-arm64'; Name = 'macos-arm64' }, @{ Rid = 'osx-x64'; Name = 'macos-x64' })
    linux = @(@{ Rid = 'linux-x64'; Name = 'linux-x64' }, @{ Rid = 'linux-arm64'; Name = 'linux-arm64' })
}

function Invoke-Dotnet([string[]]$arguments) {
    Write-Host "> dotnet $($arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) failed (exit code $LASTEXITCODE)." }
}

# The cross-platform app for one runtime: one self-contained program, in a folder of its own.
function Publish-Desktop([string]$rid, [string]$folder) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    Invoke-Dotnet @('publish', (Join-Path $root 'src/DnnManager.Desktop/DnnManager.Desktop.csproj'), '-c', $Configuration, '-r', $rid, '-nologo',
        "-p:Version=$version", "-p:AssemblyVersion=$fileVersion", "-p:FileVersion=$fileVersion", '-o', $folder)
    # Only the program: the symbols and XML docs aren't needed to run it.
    Get-ChildItem $folder -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item
}

foreach ($platform in $Platforms) {
    Write-Step "$Kind for $platform"
    try {
        if ($platform -eq 'windows') {
            if (-not $onWindows) { $notMade.Add('Windows - only on Windows (WPF, and Setup is Inno Setup).'); continue }
            foreach ($runtime in $runtimes.windows) {
                $base = "DnnManager-$version-$($runtime.Name)"
                $folder = Join-Path $out $base
                if ($Kind -eq 'setup' -and $runtime.Rid -ne 'win-x64') {
                    $notMade.Add("$base\DnnManager_Setup-$version-$($runtime.Arch).exe - Setup is 64-bit only (DnnManager.iss installs x64, the launcher is built for x64). For 32-bit Windows, choose Portable.")
                    continue
                }
                # A DNN Manager running from that folder locks the exe the build would replace: said up front, not after
                # the build. Tried by opening each exe for writing - a DNN Manager running as administrator hides its path.
                $locked = @(Get-ChildItem $folder -Filter '*.exe' -File -ErrorAction SilentlyContinue | Where-Object {
                        try { $stream = [IO.File]::Open($_.FullName, 'Open', 'ReadWrite', 'None'); $stream.Dispose(); $false } catch { $true } } | ForEach-Object Name)
                if ($locked) {
                    # Only this folder is skipped: the other processor's build still runs.
                    $failed = $true
                    $notMade.Add("$base - $($locked -join ', ') there is in use: close DNN Manager started from there, and run the task again.")
                    Write-Host "  $($locked -join ', ') in publish\$base is in use - skipped." -ForegroundColor Red
                    continue
                }
                if ($Kind -eq 'portable') {
                    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
                    # DNN Manager in Avalonia - the same app as on macOS and Linux; its publish names the exe DnnManager-<version>-<arch>.exe.
                    Invoke-Dotnet @('publish', (Join-Path $root 'src/DnnManager.Desktop/DnnManager.Desktop.csproj'), '-c', $Configuration, '-r', $runtime.Rid,
                        '--self-contained', 'true', '-nologo', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                        '-p:EnableCompressionInSingleFile=true', '-p:PortableExe=true',
                        "-p:Version=$version", "-p:AssemblyVersion=$fileVersion", "-p:FileVersion=$fileVersion", '-o', $folder)
                    # Only the portable exe: not the symbols and XML docs, nor DnnManager.exe it was copied from.
                    Get-ChildItem $folder -File | Where-Object { $_.Name -notlike 'DnnManager-*.exe' } | Remove-Item
                    $made.Add((Join-Path $folder "DnnManager-$version-$($runtime.Arch).exe"))
                    continue
                }
                # The launcher (Native AOT) links with Visual Studio's C++ build tools: without them, Setup without it.
                $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
                $cpp = (Test-Path $vswhere) -and [bool](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
                $noLauncher = @()
                if (-not $cpp) {
                    Write-Warning 'No C++ build tools here (Visual Studio''s "Desktop development with C++"): Setup is built without the launcher - fine to try out, never for a release (the release workflow always has it).'
                    $noLauncher = @('-NoLauncher')
                }
                & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\DnnManager.Installer\build.ps1') -Configuration $Configuration @noLauncher
                if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit code $LASTEXITCODE)." }
                # build.ps1 writes Setup into publish\; it goes into the platform's folder, as the others.
                $setup = Join-Path $out "DnnManager_Setup-$version-x64.exe"
                New-Item -ItemType Directory -Force $folder | Out-Null
                Move-Item $setup $folder -Force
                $made.Add((Join-Path $folder (Split-Path $setup -Leaf)))
            }
            continue
        }

        foreach ($runtime in $runtimes[$platform]) {
            $base = "DnnManager-$version-$($runtime.Name)"
            if ($Kind -eq 'portable') {
                $folder = Join-Path $out $base
                Publish-Desktop $runtime.Rid $folder
                $made.Add((Join-Path $folder 'DnnManager.Desktop'))
                continue
            }
            # A setup: the package package.ps1 makes - with the system's own tools.
            if ($platform -eq 'macos' -and -not $onMac) { $notMade.Add("$base.zip - only on a Mac (codesign, ditto). Run the Desktop workflow on GitHub, or this task on a Mac."); continue }
            if ($platform -eq 'linux' -and $onWindows) { $notMade.Add("$base.tar.gz - only on Linux or a Mac (the program's ""can run"" permission). Run the Desktop workflow on GitHub, or this task there."); continue }
            $folder = Join-Path ([IO.Path]::GetTempPath()) "dnnmanager-publish-$([guid]::NewGuid().ToString('N'))"
            try {
                Publish-Desktop $runtime.Rid $folder
                & (Join-Path $root 'src/DnnManager.Desktop/Packaging/package.ps1') -Publish $folder -Rid $runtime.Rid -Version $version -Out $out -Name $runtime.Name -Kind bundle | Out-Null
                $made.Add((Join-Path $out "$base$(if ($platform -eq 'macos') { '.zip' } else { '.tar.gz' })"))
            }
            finally { Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    catch {
        $failed = $true
        $notMade.Add("$platform - $($_.Exception.Message)")
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Step 'Done'
foreach ($file in $made) {
    $size = if (Test-Path $file) { " ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)" } else { '' }
    Write-Host "  Made      $file$size" -ForegroundColor Green
}
foreach ($line in $notMade) { Write-Host "  Not made  $line" -ForegroundColor Yellow }
if ($Kind -eq 'portable' -and ($Platforms | Where-Object { $_ -ne 'windows' })) {
    Write-Host '  On the Mac or Linux PC: chmod +x DnnManager.Desktop first - and on a Mac also: codesign --force --sign - DnnManager.Desktop' -ForegroundColor DarkGray
}
# A failure, or nothing made at all, fails the task; what only another system makes doesn't, when the rest was made.
if ($failed -or $made.Count -eq 0) { exit 1 }
