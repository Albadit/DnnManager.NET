<#
.SYNOPSIS
    Tries the files a release run built, before they go on a release: Setup installs, DNN Manager starts (through its
    launcher) and stays up with a window, the uninstaller removes it, and the portable exe starts too. Run by
    .github\workflows\release.yml (the job "Try the files"), on a runner of its own - not on a PC: it installs and
    uninstalls.

.DESCRIPTION
    1. Runs Setup silently for this user only, into a temporary folder (/CURRENTUSER /DIR=...), with its log.
    2. Checks that DnnManager.exe and the launcher (DnnManager-launcher.exe, Native AOT - no DLL beside it) are
       there and report the release's version.
    3. Starts the launcher, which starts DnnManager.exe; waits for that DnnManager.exe to have a window and checks it
       is still running a few seconds later; then ends it.
    4. Runs the uninstaller silently and waits until the installation is gone.
    5. Starts the portable exe the same way and ends it.
    A failure throws, with Setup's log and DNN Manager's log printed.
#>
[CmdletBinding()]
param(
    # The release's files: DnnManager_Setup-<version>-x64.exe and DnnManager_Portable-<version>-x64.exe.
    [Parameter(Mandatory)] [string]$Directory,
    # The tag, vX.Y.Z or vX.Y.Z-suffix.
    [Parameter(Mandatory)] [string]$Tag,
    # How long DNN Manager may take to show its window, and how long it must then keep running.
    [int]$StartSeconds = 60,
    [int]$StaySeconds = 10
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "$Tag isn't a version tag like v1.9.0." }
$version = $Tag.Substring(1)
$setup = (Resolve-Path (Join-Path $Directory "DnnManager_Setup-$version-x64.exe")).Path
$portable = (Resolve-Path (Join-Path $Directory "DnnManager_Portable-$version-x64.exe")).Path
$work = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) "dnnmanager-smoke-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$installDir = Join-Path $work 'app'
$setupLog = Join-Path $work 'setup.log'
New-Item -ItemType Directory -Force $work | Out-Null
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AD68C57A-D887-4297-A905-B6F28C1D66D1}_is1'

function Show-Logs {
    if (Test-Path $setupLog) { Write-Host '--- Setup log ---'; Get-Content $setupLog -Tail 60 | ForEach-Object { Write-Host $_ } }
    $logs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'DnnManager\logs'
    Get-ChildItem $logs -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "--- $($_.Name) ---"; Get-Content $_.FullName -Tail 60 | ForEach-Object { Write-Host $_ }
    }
}

# Starts $start (the launcher, or the portable exe) and waits for the DnnManager process running $exe to show a window
# and keep running; then ends it.
function Test-Starts([string]$start, [string]$exe, [string]$what) {
    Write-Host "Starting $($what): $start"
    $started = Start-Process -FilePath $start -WorkingDirectory (Split-Path $start) -PassThru
    $deadline = (Get-Date).AddSeconds($StartSeconds)
    $app = $null
    while ((Get-Date) -lt $deadline) {
        $app = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if ($app) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $app) {
        $code = if ($started.HasExited) { " ($start ended with exit code $($started.ExitCode))" } else { '' }
        throw "$what didn't show a window within $StartSeconds seconds$code."
    }
    Write-Host "  Window: '$($app.MainWindowTitle)' (process $($app.Id))"
    Start-Sleep -Seconds $StaySeconds
    $app.Refresh()
    if ($app.HasExited) { throw "$what closed $StaySeconds seconds after it started (exit code $($app.ExitCode))." }
    Write-Host "  Still running after $StaySeconds seconds - ending it."
    Stop-Process -Id $app.Id -Force
    $app.WaitForExit(15000) | Out-Null
}

try {
    # --- 1. Install ---
    Write-Host "Installing $setup into $installDir"
    $install = Start-Process -FilePath $setup -PassThru -Wait -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/DIR=`"$installDir`"", "/LOG=`"$setupLog`"")
    if ($install.ExitCode -ne 0) { throw "Setup ended with exit code $($install.ExitCode)." }

    # --- 2. What it installed ---
    $appExe = Join-Path $installDir 'DnnManager.exe'
    $launcher = Join-Path $installDir 'DnnManager-launcher.exe'
    foreach ($file in $appExe, $launcher) {
        if (-not (Test-Path $file)) { throw "Setup didn't install $file." }
        $product = ((Get-Item $file).VersionInfo.ProductVersion -split '\+')[0].Trim()
        if ($product -ne $version) { throw "$file reports $product, not $version." }
    }
    if (Test-Path (Join-Path $installDir 'DnnManager-launcher.dll')) { throw 'The launcher is a .NET build, not Native AOT.' }
    Write-Host "Installed $version, with the launcher."

    # --- 3. Start it as the sign-in task and the administrator prompt do: through the launcher ---
    Test-Starts $launcher $appExe 'DNN Manager (installed, through its launcher)'

    # --- 4. Uninstall ---
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (-not (Test-Path $uninstaller)) { throw "There is no uninstaller ($uninstaller)." }
    Write-Host 'Uninstalling'
    $uninstall = Start-Process -FilePath $uninstaller -PassThru -Wait -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
    if ($uninstall.ExitCode -ne 0) { throw "The uninstaller ended with exit code $($uninstall.ExitCode)." }
    # It finishes in a copy of itself: its registry key and the exe go last.
    $deadline = (Get-Date).AddSeconds(60)
    while (((Test-Path $uninstallKey) -or (Test-Path $appExe)) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $uninstallKey) { throw 'The uninstall entry is still there after 60 seconds.' }
    if (Test-Path $appExe) { throw "$appExe is still there after uninstalling." }
    Write-Host 'Uninstalled.'

    # --- 5. The portable exe ---
    Test-Starts $portable $portable 'The portable exe'
    Write-Host "The release's files install, start and uninstall."
}
catch {
    Show-Logs
    throw
}
finally {
    Get-Process -Name DnnManager, ([IO.Path]::GetFileNameWithoutExtension($portable)) -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
