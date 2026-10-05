<#
.SYNOPSIS
    Builds a portable DNN Manager one version below GitHub's newest release, to try the in-app update with.

.DESCRIPTION
    1. Asks GitHub for the newest release (the one the Update button installs) - or, when GitHub can't be reached,
       takes the newest version tag in git.
    2. Takes the version just below it: 1.6.0 -> 1.5.9, 1.7.0 -> 1.6.9, 2.0.0 -> 1.9.9.
    3. Publishes the working copy as the portable exe with that version stamped in:
       publish\update-test\DnnManager-<version>-x64.exe (an older test exe there is replaced).

    Start that exe, open a page, a project's Details and a tab, and click Update in the title bar: it downloads the
    real release, closes, installs it over the test exe and opens it again. See docs/releasing.md, The in-app update.

.EXAMPLE
    .github\scripts\build-update-test.ps1
.EXAMPLE
    .github\scripts\build-update-test.ps1 -Version 1.5.9
#>
[CmdletBinding()]
param(
    # The version to build instead of the one below the newest release.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $root 'DnnManager.csproj'
$out = Join-Path $root 'publish\update-test'

# The version just below: the last number down by one, or - at 0 - the one before it down by one and the rest 9.
function Get-VersionBelow([version]$v) {
    if ($v.Build -gt 0) { return "$($v.Major).$($v.Minor).$($v.Build - 1)" }
    if ($v.Minor -gt 0) { return "$($v.Major).$($v.Minor - 1).9" }
    if ($v.Major -gt 0) { return "$($v.Major - 1).9.9" }
    throw "There is no version below $v."
}

if (-not $Version) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $release = Invoke-RestMethod 'https://api.github.com/repos/Albadit/DnnManager.NET/releases/latest' `
            -Headers @{ 'User-Agent' = 'DnnManager'; 'Accept' = 'application/vnd.github+json' }
        $latest = [version]($release.tag_name.TrimStart('v', 'V') -split '[-+]')[0]
        Write-Host "Newest release on GitHub: $($release.tag_name)"
    }
    catch {
        $reason = $_.Exception.Message
        $tag = & git -C $root describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $tag) { throw "GitHub can't be reached ($reason) and git has no version tag." }
        $latest = [version]("$tag".Trim().TrimStart('v') -split '[-+]')[0]
        Write-Warning "GitHub can't be reached ($reason) - going one below the newest tag, $tag, instead. The Update button only shows once GitHub answers."
    }
    $Version = Get-VersionBelow $latest
}
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "'$Version' isn't a version like 1.5.9." }
$fileVersion = "$Version.0"
Write-Host "Building DNN Manager $Version for the update test"

# A test exe left from before (any version) goes - one that still runs can't, so say so.
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($old in Get-ChildItem -LiteralPath $out -Filter 'DnnManager-*-x64.exe*' -File) {
    try { Remove-Item -LiteralPath $old.FullName -Force }
    catch { throw "$($old.FullName) is in use - close that DNN Manager first." }
}

# Its own build folder, so it doesn't get in the way of a build running in the IDE.
& dotnet publish $project -nologo -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PortableExe=true `
    "-p:Version=$Version" "-p:AssemblyVersion=$fileVersion" "-p:FileVersion=$fileVersion" `
    --artifacts-path (Join-Path $root 'obj\update-test') -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $out "DnnManager-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "No $exe after publishing." }
$product = ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -split '\+')[0]
if ($product -ne $Version) { throw "$exe reports version $product, not $Version." }

Write-Host ''
Write-Host "Test exe: $exe" -ForegroundColor Green
Write-Host 'Start it, go to a page / project / tab, and click Update in the title bar (a few seconds after start).'
