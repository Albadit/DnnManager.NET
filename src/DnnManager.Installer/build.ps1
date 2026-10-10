<#
.SYNOPSIS
    Builds the DNN Manager installer: publish\DnnManager_Setup-<version>-x64.exe

.DESCRIPTION
    1. Publishes the app self-contained for win-x64 as a single file into src\DnnManager.Installer\bin\app.
    2. Draws the wizard images from the app icon into src\DnnManager.Installer\bin\images.
    3. Compiles src\DnnManager.Installer\DnnManager.iss with Inno Setup's ISCC.exe - an installed Inno Setup 6
       when there is one, otherwise (and always with -PinnedInno, as releases do) a pinned copy from nuget.org
       (Tools.InnoSetup): the package is cached in src\DnnManager.Installer\bin\tools and checked against its
       SHA-512 on every build, and the tools are extracted from it again each time.
    4. With -SignScript, signs the published DnnManager.exe and the launcher, and has Inno sign Setup and its
       uninstaller.

    The launcher (src\DnnManager.Launcher, DnnManager-launcher.exe) is published with Native AOT into
    src\DnnManager.Installer\bin\launcher and installed beside DnnManager.exe: the sign-in task and Windows'
    administrator prompt start DNN Manager through it, without the .NET variables of the user's environment. Native
    AOT needs the C++ build tools (Visual Studio's "Desktop development with C++", or its Build Tools); -NoLauncher
    builds a Setup without it, for a PC that has none - never a release.

    Everything the build makes along the way is in src\DnnManager.Installer\bin; the finished Setup is in
    publish\ (next to DnnManager.csproj). The version is the newest version tag in git (v1.7.1 -> 1.7.1), as for
    every local build, unless -Version is given (the release workflow, .github\workflows\release.yml, passes the
    tag's version).

.EXAMPLE
    .\src\DnnManager.Installer\build.ps1
.EXAMPLE
    .\src\DnnManager.Installer\build.ps1 -SkipPublish -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
.EXAMPLE
    .\src\DnnManager.Installer\build.ps1 -Version 1.7.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # ISCC.exe to use; found automatically when omitted.
    [string]$Iscc,
    # Always the pinned Inno Setup from nuget.org, never one installed here - the same compiler for every release.
    [switch]$PinnedInno,
    # Reuse bin\app (and bin\launcher) from an earlier run - the release workflow's sign job compiles Setup from what
    # its build job published.
    [switch]$SkipPublish,
    # Only publishes bin\app and bin\launcher, unsigned, and stops (the release workflow's build job).
    [switch]$PublishOnly,
    # The version to build (1.7.0, or 1.7.0-rc.1 for a pre-release) instead of the newest version tag.
    [string]$Version,
    # A script that signs a file (Authenticode): called as <script> -Path <file>, and throws when it fails. With it,
    # the published DnnManager.exe is signed before it goes into Setup, and Inno signs Setup and its uninstaller
    # (the release workflow passes .github\scripts\sign.ps1 when signing is set up). Without it nothing is signed.
    [string]$SignScript,
    # Leaves the launcher out (no C++ build tools here): DNN Manager is then started as up to 1.8.1.
    [switch]$NoLauncher
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The repository root: this script is in src\DnnManager.Installer.
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $root 'DnnManager.csproj'
$script = Join-Path $PSScriptRoot 'DnnManager.iss'
$icon = Join-Path $root 'src\DnnManager.Presentation\Assets\dnn.ico'
# bin (next to this script): intermediate files (published app, wizard images, Inno Setup). publish: the Setup exe.
$binDir = Join-Path $PSScriptRoot 'bin'
$publishDir = Join-Path $binDir 'app'
$launcherProject = Join-Path $root 'src\DnnManager.Launcher\DnnManager.Launcher.csproj'
$launcherDir = Join-Path $binDir 'launcher'
$imagesDir = Join-Path $binDir 'images'
$outputDir = Join-Path $root 'publish'
$innoVersion = '6.7.3'
# nuget.org's SHA-512 of Tools.InnoSetup $innoVersion (its catalog's packageHash): a download that isn't it isn't run.
$innoSha512 = 'rdSE25v0Im6KKD2sH275qceul818uExubvAWG4BbHHuEAwnknapphEuwqOV77F3JunurFXF9vSfZJp7wkuBUxA=='

# The version a local build gets: the newest version tag (the project's VersionFromGitTag target).
function Get-AppVersion {
    $version = & dotnet msbuild $project -nologo -t:VersionFromGitTag -getProperty:Version
    if ($LASTEXITCODE -ne 0 -or -not $version) { throw "Can't read the version of $project (exit code $LASTEXITCODE)." }
    return "$version".Trim()
}

function Invoke-Publish {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    # Single file, uncompressed (the installer compresses it) and with the native DLLs beside the exe,
    # so nothing is extracted to %TEMP% when the app starts.
    & dotnet publish $project -nologo -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
        "-p:Version=$version" "-p:AssemblyVersion=$fileVersion" "-p:FileVersion=$fileVersion" `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
}

# The launcher, compiled ahead of time (Native AOT): a native exe without CoreCLR, so nothing in the environment it is
# started with loads code into it. Returns its path.
function Invoke-PublishLauncher {
    if (Test-Path $launcherDir) { Remove-Item $launcherDir -Recurse -Force }
    & dotnet publish $launcherProject -nologo -c $Configuration -r win-x64 `
        "-p:Version=$version" "-p:AssemblyVersion=$fileVersion" "-p:FileVersion=$fileVersion" `
        -o $launcherDir
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the launcher failed (exit code $LASTEXITCODE). Native AOT needs the C++ build tools (Visual Studio's " +
            "'Desktop development with C++'); without them, build with -NoLauncher (not for a release)."
    }
    $exe = Join-Path $launcherDir 'DnnManager-launcher.exe'
    if (-not (Test-Path $exe)) { throw "No DnnManager-launcher.exe in $launcherDir." }
    # A .NET build of it (a DLL and a runtimeconfig beside the exe) would run on CoreCLR - the very thing it avoids.
    if (Test-Path (Join-Path $launcherDir 'DnnManager-launcher.dll')) { throw "$exe isn't a Native AOT build (a DnnManager-launcher.dll is beside it)." }
    return $exe
}

# The wizard's large image (welcome / finish pages) and small one (top right of the other pages): the app
# icon on white, at 100% and 200% so it stays sharp on high-DPI screens.
function New-WizardImages {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force $imagesDir | Out-Null

    # The icon's largest frame. A 256px frame is usually stored as PNG, which Icon.ToBitmap can't read on
    # .NET Framework, so it is taken from the .ico's directory directly.
    $bytes = [IO.File]::ReadAllBytes($icon)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = 0..($count - 1) | Sort-Object { $w = $bytes[6 + $_ * 16]; if ($w -eq 0) { 256 } else { $w } } -Descending | Select-Object -First 1
    $size = [BitConverter]::ToInt32($bytes, 6 + $best * 16 + 8)
    $offset = [BitConverter]::ToInt32($bytes, 6 + $best * 16 + 12)
    $isPng = $bytes[$offset] -eq 0x89 -and $bytes[$offset + 1] -eq 0x50

    function Save-Image([int]$width, [int]$height, [int]$iconSize, [string]$name) {
        $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        if ($isPng) {
            $stream = New-Object IO.MemoryStream (, [byte[]]$bytes[$offset..($offset + $size - 1)])
            $image = [System.Drawing.Image]::FromStream($stream)
        }
        else {
            $stream = $null
            $image = (New-Object System.Drawing.Icon $icon, 256, 256).ToBitmap()
        }
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $x = [int](($width - $iconSize) / 2)
            $y = [int](($height - $iconSize) / 2)
            $graphics.DrawImage($image, $x, $y, $iconSize, $iconSize)
            $bitmap.Save((Join-Path $imagesDir $name), [System.Drawing.Imaging.ImageFormat]::Bmp)
        }
        finally {
            $image.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
            if ($stream) { $stream.Dispose() }
        }
    }

    Save-Image 164 314 112 'wizard-100.bmp'
    Save-Image 328 628 224 'wizard-200.bmp'
    Save-Image 55 55 48 'wizard-small-100.bmp'
    Save-Image 110 110 96 'wizard-small-200.bmp'
}

function Find-Iscc {
    if ($Iscc) {
        if (-not (Test-Path $Iscc)) { throw "ISCC.exe not found at $Iscc." }
        return $Iscc
    }
    if (-not $PinnedInno) {
        $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if ($onPath) { return $onPath.Source }
        $installed = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($installed) { return $installed }
    }

    # The NuGet package's copy (no install, no admin rights). Only the package is kept between builds; the tools are
    # extracted from it again on every build, from the very bytes whose SHA-512 was just checked (read once, into
    # memory) - an extracted ISCC.exe, Setup.e32 or SetupLdr.e64 that something changed since the last build is
    # never run. (ISCC also checks its own DLLs and .e32 files against the .issig files beside them.)
    $cacheDir = Join-Path $binDir 'tools'
    $package = Join-Path $cacheDir "Tools.InnoSetup.$innoVersion.nupkg"
    $toolDir = Join-Path $cacheDir "innosetup-$innoVersion"
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $bytes = if (Test-Path $package) { [IO.File]::ReadAllBytes($package) } else { $null }
    if (-not $bytes -or (Get-Sha512 $bytes) -ne $innoSha512) {
        Write-Host "Downloading Tools.InnoSetup $innoVersion from nuget.org..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $download = "$package.download"
        Invoke-WebRequest -UseBasicParsing "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" -OutFile $download
        $bytes = [IO.File]::ReadAllBytes($download)
        Remove-Item $download -Force
        if ((Get-Sha512 $bytes) -ne $innoSha512) {
            throw "Tools.InnoSetup $innoVersion from nuget.org isn't the package it should be (its SHA-512 differs) - not used."
        }
        [IO.File]::WriteAllBytes($package, $bytes)
    }

    if (Test-Path $toolDir) { Remove-Item $toolDir -Recurse -Force }
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    $zip = New-Object IO.Compression.ZipArchive (New-Object IO.MemoryStream (, $bytes)), ([IO.Compression.ZipArchiveMode]::Read)
    try {
        # Only the compiler's folder; a name that would land outside it is refused.
        $target = [IO.Path]::GetFullPath($toolDir) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith('tools/') -or $entry.FullName.EndsWith('/')) { continue }
            $path = [IO.Path]::GetFullPath((Join-Path $toolDir ([Uri]::UnescapeDataString($entry.FullName))))
            if (-not $path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { throw "Tools.InnoSetup has an entry outside its folder: $($entry.FullName)" }
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            $in = $entry.Open()
            $out = [IO.File]::Create($path)
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $compiler = Join-Path $toolDir 'tools\ISCC.exe'
    if (-not (Test-Path $compiler)) { throw "Tools.InnoSetup $innoVersion has no tools\ISCC.exe." }
    return $compiler
}

# SHA-512 as nuget.org writes it: base64.
function Get-Sha512([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA512]::Create()
    try { return [Convert]::ToBase64String($sha.ComputeHash($bytes)) }
    finally { $sha.Dispose() }
}

# Signs a file with -SignScript (Authenticode) - it throws when signing fails.
function Invoke-Sign([string]$path) {
    Write-Host "Signing $path"
    & $SignScript -Path $path
    if (-not $?) { throw "Signing $path failed." }
}

$version = if ($Version) { $Version.TrimStart('v') } else { Get-AppVersion }
if ($version -notmatch '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "'$version' isn't a version like 1.7.0 or 1.7.0-rc.1." }
# Windows' file version is four numbers without a pre-release suffix: 1.7.0-rc.1 -> 1.7.0.0.
$fileVersion = "$($Matches[1]).0"
Write-Host "DNN Manager $version"

if ($SignScript) {
    $SignScript = (Resolve-Path $SignScript).Path
    if ($SignScript -match '[$"]') { throw "-SignScript's path can't contain `$ or a quote: $SignScript" }
}
if (-not $SkipPublish) { Invoke-Publish }
$appExe = Join-Path $publishDir 'DnnManager.exe'
if (-not (Test-Path $appExe)) { throw "No DnnManager.exe in $publishDir - run without -SkipPublish." }
$launcherExe = $null
if ($NoLauncher) { Write-Warning 'Built without the launcher (-NoLauncher): the sign-in task and the administrator prompt start DnnManager.exe itself.' }
elseif (-not $SkipPublish) { $launcherExe = Invoke-PublishLauncher }
else {
    $launcherExe = Join-Path $launcherDir 'DnnManager-launcher.exe'
    if (-not (Test-Path $launcherExe)) { throw "No DnnManager-launcher.exe in $launcherDir - run without -SkipPublish, or with -NoLauncher." }
    if (Test-Path (Join-Path $launcherDir 'DnnManager-launcher.dll')) { throw "$launcherExe isn't a Native AOT build." }
}
if ($PublishOnly) {
    Write-Host "Published $appExe$(if ($launcherExe) { " and $launcherExe" }) - Setup isn't compiled (-PublishOnly)."
    return
}
# The exes go into Setup signed: what is installed carries the signature, not only Setup.
if ($SignScript) {
    Invoke-Sign $appExe
    if ($launcherExe) { Invoke-Sign $launcherExe }
}

New-WizardImages
$compiler = Find-Iscc
$isccArgs = @('/Q', "/DAppVersion=$version", "/DFileVersion=$fileVersion", "/DPublishDir=$publishDir", "/DOutputDir=$outputDir", "/DImagesDir=$imagesDir")
if ($launcherExe) { $isccArgs += "/DLauncherPath=$launcherExe" }
if ($SignScript) {
    # Inno's sign tool "dnnsign" (DnnManager.iss: SignTool={#SignToolName} and SignedUninstaller=yes when SignToolName
    # is defined) signs Setup and the uninstaller inside it. Inno puts the file's quoted path for $f and a quote for $q.
    $isccArgs += '/DSignToolName=dnnsign'
    $isccArgs += '/Sdnnsign=powershell.exe -NoProfile -ExecutionPolicy Bypass -File $q' + $SignScript + '$q -Path $f'
}
Write-Host "Compiling the installer with $compiler"
& $compiler @isccArgs $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $outputDir "DnnManager_Setup-$version-x64.exe"
if ($SignScript) {
    # ISCC ignores /DSignToolName when DnnManager.iss doesn't use it: a Setup that should be signed and isn't is an error.
    $signature = Get-AuthenticodeSignature $setup
    if ($signature.Status -ne 'Valid') {
        throw "$setup isn't signed ($($signature.Status)) - DnnManager.iss needs SignTool={#SignToolName} and SignedUninstaller=yes when SignToolName is defined."
    }
}
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
