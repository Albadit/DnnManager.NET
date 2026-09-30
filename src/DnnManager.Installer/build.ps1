<#
.SYNOPSIS
    Builds the DNN Manager installer: publish\DnnManagerSetup-<version>-x64.exe

.DESCRIPTION
    1. Publishes the app self-contained for win-x64 as a single file into src\DnnManager.Installer\bin\app.
    2. Draws the wizard images from the app icon into src\DnnManager.Installer\bin\images.
    3. Compiles src\DnnManager.Installer\DnnManager.iss with Inno Setup's ISCC.exe - an installed Inno Setup 6
       when there is one, otherwise a pinned copy from nuget.org (Tools.InnoSetup) cached in
       src\DnnManager.Installer\bin\tools.

    Everything the build makes along the way is in src\DnnManager.Installer\bin; the finished Setup is in
    publish\ (next to DnnManager.csproj). The version comes from <Version> in DnnManager.csproj - raise it there
    for a new release.

.EXAMPLE
    .\src\DnnManager.Installer\build.ps1
.EXAMPLE
    .\src\DnnManager.Installer\build.ps1 -SkipPublish -Iscc 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # ISCC.exe to use; found automatically when omitted.
    [string]$Iscc,
    # Reuse bin\app from an earlier run.
    [switch]$SkipPublish
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
$imagesDir = Join-Path $binDir 'images'
$outputDir = Join-Path $root 'publish'
$innoVersion = '6.7.3'

function Get-AppVersion {
    [xml]$xml = Get-Content -Raw $project
    $version = $xml.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "No <Version> in $project." }
    return $version.InnerText.Trim()
}

function Invoke-Publish {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    # Single file, uncompressed (the installer compresses it) and with the native DLLs beside the exe,
    # so nothing is extracted to %TEMP% when the app starts.
    & dotnet publish $project -nologo -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
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
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $installed = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($installed) { return $installed }

    # No Inno Setup on this PC: use the NuGet package's copy (no install, no admin rights).
    $toolDir = Join-Path $binDir "tools\innosetup-$innoVersion"
    $cached = Join-Path $toolDir 'tools\ISCC.exe'
    if (-not (Test-Path $cached)) {
        Write-Host "Inno Setup not found - downloading Tools.InnoSetup $innoVersion from nuget.org..."
        New-Item -ItemType Directory -Force $toolDir | Out-Null
        $package = Join-Path $toolDir 'package.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" -OutFile $package
        Expand-Archive $package -DestinationPath $toolDir -Force
        Remove-Item $package
    }
    return $cached
}

$version = Get-AppVersion
Write-Host "DNN Manager $version"

if (-not $SkipPublish) { Invoke-Publish }
if (-not (Test-Path (Join-Path $publishDir 'dnnmanager.exe'))) { throw "No dnnmanager.exe in $publishDir - run without -SkipPublish." }

New-WizardImages
$compiler = Find-Iscc
Write-Host "Compiling the installer with $compiler"
& $compiler /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" "/DImagesDir=$imagesDir" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Join-Path $outputDir "DnnManagerSetup-$version-x64.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
