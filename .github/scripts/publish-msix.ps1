<#
.SYNOPSIS
    Adds the MSIX package to a DNN Manager release on GitHub: pick the release, and it builds, signs and uploads
    DnnManager_<version>_x64.msix.

.DESCRIPTION
    The release itself - its tag, notes, Setup and portable exe - is made by publish-release.ps1 (the task
    "release (GitHub)"). This adds the package to it afterwards, as a release of its own kind:
    1. Lists the releases on GitHub (published and drafts); the newest is the default.
    2. Checks out the release's tag into a temporary worktree (your working copy is not touched) and builds the
       package with src\DnnManager.Package\build.ps1, signed with your code-signing certificate.
    3. Checks the package's version and that its signature is trusted - never a self-signed one, which installs only
       on a PC that trusts it. It lands in publish\vX.Y.Z.
    4. After you confirm: uploads it to the release (replacing a package the release has already) and checks it.

    The certificate: -SigningThumbprint, or the environment variable DNNMANAGER_SIGNING_THUMBPRINT - a code-signing
    certificate Windows trusts, in CurrentUser\My or LocalMachine\My (a hardware token's or a cloud HSM's included).
    It signs in to GitHub with the credential Git already uses for this repository. The VS Code task
    "release: MSIX (GitHub)" asks for the release in VS Code's picker (filled by -List); run directly, it asks in the
    terminal. See .docs\releasing.md, The MSIX package.

.EXAMPLE
    .github\scripts\publish-msix.ps1
.EXAMPLE
    .github\scripts\publish-msix.ps1 -Tag v1.8.0 -SigningThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    # The release to add the package to (v1.8.0); asked for when omitted.
    [string]$Tag,
    # The thumbprint of the code-signing certificate. Default: the environment variable DNNMANAGER_SIGNING_THUMBPRINT.
    [string]$SigningThumbprint = $env:DNNMANAGER_SIGNING_THUMBPRINT,
    # Only prints the choices for VS Code's picker (Tasks Shell Input), one "value||label||description||detail" per line.
    [ValidateSet('releases')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# --- Helpers (as in publish-release.ps1) ---

# Runs a program and throws when it fails. -Quiet returns its output instead of showing it as it runs.
function Invoke-Tool([string]$exe, [string[]]$argv, [switch]$Quiet) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { $out = @(& $exe @argv 2>&1 | ForEach-Object { "$_" }) }
        else {
            Write-Host "> $exe $($argv -join ' ')" -ForegroundColor DarkGray
            & $exe @argv 2>&1 | ForEach-Object { Write-Host "$_" }
        }
    }
    finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        if ($Quiet) { $out | Write-Host }
        throw "$exe $($argv | Select-Object -First 3) failed (exit code $LASTEXITCODE)."
    }
    if ($Quiet) { return $out }
}

function Invoke-Git([string[]]$argv) { Invoke-Tool git (@('-C', $root) + $argv) -Quiet }

function Read-Choice([string]$title, [string[]]$items, [int]$default) {
    Write-Host ''
    Write-Host $title -ForegroundColor Cyan
    for ($i = 0; $i -lt $items.Count; $i++) {
        $mark = if ($i -eq $default) { '*' } else { ' ' }
        Write-Host ("{0} {1,2}. {2}" -f $mark, ($i + 1), $items[$i])
    }
    while ($true) {
        $answer = Read-Host "Choose 1-$($items.Count) (Enter = $($default + 1))"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $items.Count) { return $n - 1 }
        Write-Host 'Not one of the numbers above.' -ForegroundColor Yellow
    }
}

function Confirm-Step([string]$question) {
    $answer = Read-Host "$question [y/N]"
    return $answer -match '^(y|yes)$'
}

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

# GitHub's REST API, signed in with the credential Git uses for github.com (never printed). A read without one is
# anonymous - the repository is public; it just doesn't see drafts.
$script:token = $null
function Invoke-GitHub([string]$method, [string]$url, [string]$inFile, [string]$contentType = 'application/json; charset=utf-8') {
    if ($null -eq $script:token) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $env:GIT_TERMINAL_PROMPT = '0'
        try { $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null } finally { $ErrorActionPreference = $prev }
        $script:token = "$(($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', '')"
    }
    if (-not $script:token -and $method -ne 'GET') { throw 'No GitHub credential found - sign in to GitHub with Git first (e.g. git push once).' }
    if ($url -notmatch '^https://') { $url = "https://api.github.com/repos/$script:repo$url" }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if ($script:token) { $headers.Authorization = "Bearer $script:token" }
    $params = @{ Method = $method; Uri = $url; Headers = $headers; ContentType = $contentType; UseBasicParsing = $true }
    if ($inFile) { $params.InFile = $inFile; $params.TimeoutSec = 1800 }
    return Invoke-RestMethod @params
}

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]

# GitHub's releases, newest first (drafts included - they aren't found by tag). Kept in a variable first: Windows
# PowerShell 5.1 passes a JSON array on as one object.
function Get-Releases {
    $all = Invoke-GitHub GET '/releases?per_page=30'
    @(foreach ($r in $all) { $r })
}

function Get-PackageAsset($release, [string]$name) {
    @($release.assets) | Where-Object { $_.name -eq $name } | Select-Object -First 1
}

if ($List) {
    # Output for VS Code's picker: value||label||description||detail.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($r in Get-Releases) {
        $version = ([string]$r.tag_name).TrimStart('v')
        $has = if (Get-PackageAsset $r "DnnManager_$($version)_x64.msix") { 'has a package - it is replaced' } else { 'no package yet' }
        $state = if ($r.draft) { 'draft' } elseif ($r.prerelease) { 'pre-release' } else { 'published' }
        "$($r.tag_name)||`$(package) $($r.tag_name)||$state||$has"
    }
    return
}

Write-Host "DNN Manager MSIX package - $script:repo" -ForegroundColor Cyan

# --- 1. The release ---

$releases = @(Get-Releases)
if ($releases.Count -eq 0) { throw 'GitHub has no releases - make one with the task "release (GitHub)" first.' }
if ($Tag) {
    $Tag = 'v' + $Tag.TrimStart('v', 'V')
    $release = $releases | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    if (-not $release) { throw "GitHub has no release $Tag among its newest 30 - make it with the task ""release (GitHub)"" first." }
}
else {
    $labels = $releases | ForEach-Object { "$($_.tag_name)$(if ($_.draft) { '  (draft)' } elseif ($_.prerelease) { '  (pre-release)' })" }
    $release = $releases[(Read-Choice 'Release to add the package to:' $labels 0)]
    $Tag = $release.tag_name
}
if ($Tag -notmatch '^v((\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?)$') { throw "$Tag isn't a version tag like v1.8.0." }
$version = $Matches[1]
$fileVersion = "$($Matches[2]).0"
$msixName = "DnnManager_$($version)_x64.msix"

# --- 2. Checks before building ---

Write-Step 'Checks'
# Uploading needs it - known now, not after the build.
if (-not $script:token) { throw 'No GitHub credential found - sign in to GitHub with Git first (e.g. git push once).' }
if (-not $SigningThumbprint) {
    throw 'No signing certificate: give -SigningThumbprint, or set the environment variable DNNMANAGER_SIGNING_THUMBPRINT - see .docs\releasing.md, The MSIX package.'
}
$null = Invoke-Git @('fetch', '--quiet', '--tags', 'origin')
$sha = (Invoke-Git @('rev-parse', '--verify', "refs/tags/$Tag^{commit}")) | Select-Object -First 1
$existing = Get-PackageAsset $release $msixName
Write-Host "  Release  $Tag$(if ($release.draft) { ' (draft)' }) - commit $($sha.Substring(0, 7))"
if ($existing) { Write-Host "  It has $msixName already - it is replaced after you confirm." -ForegroundColor Yellow }

# --- 3. Build in a worktree of the tag ---

$work = Join-Path $env:TEMP "dnnmanager-msix-$version"
$out = Join-Path $root "publish\$Tag"
$msix = Join-Path $out $msixName
try {
    Write-Step "Building the package of $Tag"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    $null = Invoke-Git @('worktree', 'prune')
    $null = Invoke-Git @('worktree', 'add', '--detach', $work, $sha)

    $package = Join-Path $work 'src\DnnManager.Package\build.ps1'
    # An older DNN Manager in a package would try to update itself into the package's read-only folder.
    if (-not (Test-Path $package)) { throw "$Tag is older than the MSIX package (no src\DnnManager.Package) - release a newer version first." }

    # The version goes into the manifest too (the exe's file properties), as publish-release.ps1 does.
    $appManifest = Join-Path $work 'app.manifest'
    [IO.File]::WriteAllText($appManifest, ([IO.File]::ReadAllText($appManifest) -replace '(<assemblyIdentity version=")[0-9.]+(")', "`${1}$fileVersion`${2}"), [Text.UTF8Encoding]::new($false))

    $null = Invoke-Tool powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $package, '-Version', $version, '-CertificateThumbprint', $SigningThumbprint)

    # --- 4. Check the package ---
    Write-Step 'Package'
    $null = New-Item -ItemType Directory -Force $out
    Copy-Item (Join-Path $work "publish\$msixName") $msix -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($msix)
    try { $appx = [xml](New-Object IO.StreamReader ($zip.GetEntry('AppxManifest.xml').Open())).ReadToEnd() }
    finally { $zip.Dispose() }
    $packageVersion = $appx.Package.Identity.Version
    $signature = Get-AuthenticodeSignature $msix
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { 'nobody' }
    Write-Host ("  {0}: package version {1}, signed by {2} ({3})" -f $msixName, $packageVersion, $signer, $signature.Status)
    if ($packageVersion -ne $fileVersion) { throw "$msixName is package version $packageVersion, not $fileVersion." }
    # A self-signed certificate reads as valid on a PC that trusts it (as one does for testing) - and nowhere else.
    if ($signature.SignerCertificate -and $signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer) {
        throw "$msixName is signed with a self-signed certificate ($signer) - users couldn't install it."
    }
    if ($signature.Status -ne 'Valid') { throw "$msixName's signature isn't trusted ($($signature.Status): $($signature.StatusMessage)) - users couldn't install it." }
}
finally {
    if (Test-Path $work) {
        try { $null = Invoke-Git @('worktree', 'remove', '--force', $work) }
        catch { Write-Host "  Couldn't remove the worktree $work - delete it and run git worktree prune." -ForegroundColor Yellow }
    }
}

# --- 5. Upload ---

Write-Step 'Ready to upload'
Write-Host "  Release  $Tag$(if ($release.draft) { ' (draft)' })"
Write-Host "  File     $msixName ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB)$(if ($existing) { ' - replaces the one it has' })"
Write-Host "           in $out"
if (-not (Confirm-Step "Upload it to $Tag on GitHub now?")) {
    Write-Host "Not uploaded. The package stays in $out." -ForegroundColor Yellow
    return
}

if ($existing) {
    $null = Invoke-GitHub DELETE "/releases/assets/$($existing.id)"
    Write-Host "  Removed the release's earlier $msixName."
}
Write-Host "  Uploading $msixName..."
$asset = Invoke-GitHub POST "https://uploads.github.com/repos/$script:repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($msixName))" $msix 'application/octet-stream'
if ($asset.size -ne (Get-Item $msix).Length) { throw "$msixName arrived with $($asset.size) bytes, expected $((Get-Item $msix).Length) - check the release." }

Write-Host ''
Write-Host "Added $msixName to $($release.html_url)" -ForegroundColor Green
