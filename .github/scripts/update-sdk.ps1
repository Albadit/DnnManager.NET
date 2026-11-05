<#
.SYNOPSIS
    Moves the repository to the newest .NET SDK installed on this PC: global.json, and the lock files that follow it.

.DESCRIPTION
    global.json names one SDK exactly (rollForward: disable), and that SDK decides the versions of the packages it
    brings itself - the Native AOT compiler the launcher restores (Microsoft.DotNet.ILCompiler) and the trimmer
    (Microsoft.NET.ILLink.Tasks) - which packages.lock.json records. Once a .NET security patch is out, the older
    compiler is a known vulnerability and every restore fails with NU1903. This script does the whole move:

    1. Asks Microsoft's release list for the newest SDK of global.json's channel (10.0, say) and, with -Install,
       installs it with winget when this PC doesn't have it yet.
    2. Puts the newest installed SDK of that channel (or -Version) in global.json.
    3. Updates the lock files with that SDK (dotnet restore --force-evaluate): the app's, the tests' and the
       launcher's.
    4. Lists any package with a known vulnerability, and the files that changed - commit them, then release.

    Run again with nothing new, it changes nothing. CI and the release workflow install the SDK global.json names,
    so they follow the commit.

.EXAMPLE
    .github\scripts\update-sdk.ps1 -Install
.EXAMPLE
    .github\scripts\update-sdk.ps1 -Version 10.0.403
#>
[CmdletBinding()]
param(
    # The SDK to move to; the newest installed one of global.json's channel when omitted.
    [string]$Version,
    # Installs the newest SDK of the channel with winget when this PC doesn't have it (asks for administrator rights).
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$globalJson = Join-Path $root 'global.json'
$restore = @('tests\DnnManager.IntegrationTests', 'src\DnnManager.Launcher')

Import-Module (Join-Path $PSScriptRoot 'ReleaseCommon.psm1') -Force
Set-ReleaseContext -Root $root

$text = [IO.File]::ReadAllText($globalJson)
$current = (ConvertFrom-Json $text).sdk.version
if ($current -notmatch '^(\d+)\.(\d+)\.\d+$') { throw "global.json names SDK '$current' - expected a release such as 10.0.403." }
$major = $Matches[1]
$channel = "$($Matches[1]).$($Matches[2])"

# The installed SDKs of the channel - releases only, no previews.
function Get-InstalledSdks {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $lines = @(& dotnet --list-sdks 2>$null) } finally { $ErrorActionPreference = $prev }
    @($lines | ForEach-Object { ($_ -split ' ', 2)[0] } |
            Where-Object { $_ -match "^$([regex]::Escape($channel))\.\d+$" } |
            Sort-Object { [version]$_ } -Descending)
}

Write-Host ".NET SDK - global.json names $current (channel $channel)" -ForegroundColor Cyan

# --- 1. The newest SDK Microsoft has released, installed when asked ---

$latest = $null
try {
    $releases = Invoke-RestMethod -UseBasicParsing "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$channel/releases.json"
    $latest = [string]$releases.'latest-sdk'
    Write-Host "  Newest released: SDK $latest (runtime $($releases.'latest-runtime'), $($releases.'latest-release-date'))"
}
catch { Write-Host "  Microsoft's release list couldn't be read ($($_.Exception.Message)) - using the SDKs installed here." -ForegroundColor Yellow }

$installed = @(Get-InstalledSdks)
$newestInstalled = if ($installed.Count -gt 0) { $installed[0] } else { $null }
Write-Host "  Installed here:  $(if ($installed.Count -gt 0) { $installed -join ', ' } else { "no $channel SDK" })"

if ($latest -and $latest -match '^\d+\.\d+\.\d+$' -and (-not $newestInstalled -or [version]$latest -gt [version]$newestInstalled)) {
    if ($Install) {
        Write-Step "Installing .NET SDK $latest"
        Invoke-Tool winget @('install', '--id', "Microsoft.DotNet.SDK.$major", '--exact', '--silent',
            '--accept-package-agreements', '--accept-source-agreements')
        $installed = @(Get-InstalledSdks)
        $newestInstalled = if ($installed.Count -gt 0) { $installed[0] } else { $null }
    }
    else {
        Write-Host "  SDK $latest isn't installed here - run with -Install (the task does), or install it from" -ForegroundColor Yellow
        Write-Host "  https://dotnet.microsoft.com/download/dotnet/$channel" -ForegroundColor Yellow
    }
}

# --- 2. global.json ---

$target = if ($Version) { $Version } else { $newestInstalled }
if (-not $target) { throw "No .NET $channel SDK is installed - install one (or run with -Install)." }
if ($installed -notcontains $target) { throw "SDK $target isn't installed here (installed: $($installed -join ', '))." }
if ([version]$target -lt [version]$current) {
    Write-Host "  SDK $target is older than global.json's $current - nothing changed." -ForegroundColor Yellow
    return
}

Write-Step 'global.json'
if ($target -eq $current) { Write-Host "  SDK $current already - the lock files are checked against it." }
else {
    # Only the version changes: the file keeps its layout and line endings, UTF-8 without BOM.
    $updated = [regex]::Replace($text, '("version"\s*:\s*")[^"]*(")', "`${1}$target`${2}", 1)
    [IO.File]::WriteAllText($globalJson, $updated, (New-Object Text.UTF8Encoding $false))
    Write-Host "  SDK $current -> $target"
}

# --- 3. The lock files, restored with that SDK (dotnet picks it from global.json in the repository's folder) ---

Push-Location $root
try {
    $used = (Invoke-Tool dotnet @('--version') -Quiet) | Select-Object -First 1
    if ($used -ne $target) { throw "dotnet uses SDK $used here, not $target - is global.json in $root?" }

    Write-Step 'Lock files'
    foreach ($project in $restore) { Invoke-Tool dotnet @('restore', $project, '--force-evaluate') }

    # --- 4. Known vulnerabilities, and what changed ---

    Write-Step 'Known vulnerabilities'
    $vulnerable = $false
    foreach ($project in @('DnnManager.csproj', 'src\DnnManager.Launcher')) {
        $out = @(Invoke-Tool dotnet @('list', $project, 'package', '--vulnerable', '--include-transitive') -Quiet)
        if ($out -match 'has the following vulnerable packages') {
            $vulnerable = $true
            $out | Where-Object { $_ -match '^\s*[>|]|vulnerable' } | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }
        else { Write-Host "  $project - none" }
    }
}
finally { Pop-Location }

Write-Step 'Changed'
$changed = @(Invoke-Git @('status', '--short', '--', 'global.json', '*packages.lock.json'))
if ($changed.Count -eq 0) { Write-Host '  Nothing - the repository is on the newest installed SDK already.' }
else {
    $changed | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host '  Run the fast tests, commit and push these, then release that commit. A commit message:' -ForegroundColor Cyan
    Write-Host "  fix: build with .NET SDK $target"
}
if ($vulnerable) {
    Write-Host ''
    Write-Host '  A package above still has a known vulnerability - it is one of the project''s own (a PackageReference):' -ForegroundColor Yellow
    Write-Host '  raise its version in its .csproj, then run this again.' -ForegroundColor Yellow
}
