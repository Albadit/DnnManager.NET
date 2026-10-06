<#
.SYNOPSIS
    Publishes DNN Manager's MSIX package as a GitHub release of its own: pick a release-notes file and a commit, and it
    builds, signs, tags and publishes DnnManager_<version>_x64.msix.

.DESCRIPTION
    The MSIX release is apart from the exe release (publish-release.ps1, the task "release (GitHub)"): its tag is
    msix-vX.Y.Z, and it is never GitHub's "latest" release - DNN Manager's own update and Setup only look at the latest
    release, for Setup and the portable exe, so they never see this one. The version's exe release (vX.Y.Z) can come
    before, after or not at all.
    1. Lists .docs\release-notes\vX.Y.Z.md - the file's name is the version; the notes are the release's.
    2. Lists the recent commits of the current branch on GitHub; the newest is the default.
    3. Checks out that commit into a temporary worktree (your working copy is not touched), runs the fast tests and
       builds the package with src\DnnManager.Package\build.ps1, signed with your code-signing certificate.
    4. Checks the package's version and that its signature is trusted - never a self-signed one, which installs only on
       a PC that trusts it. It lands in publish\msix-vX.Y.Z.
    5. After you confirm: tags the commit msix-vX.Y.Z, pushes the tag, creates the release as a draft with the notes,
       uploads the package, checks it and publishes it - not as the latest release.

    The certificate: -SigningThumbprint, or the environment variable DNNMANAGER_SIGNING_THUMBPRINT - a code-signing
    certificate Windows trusts, in CurrentUser\My or LocalMachine\My (a hardware token's or a cloud HSM's included).
    It signs in to GitHub with the credential Git already uses for this repository. The VS Code task
    "release: MSIX (GitHub)" asks for the notes file and the commit in VS Code's pickers (filled by -List); run
    directly, it asks in the terminal. See .docs\releasing.md, The MSIX package.

.EXAMPLE
    .github\scripts\publish-msix.ps1
.EXAMPLE
    .github\scripts\publish-msix.ps1 -NotesFile .docs\release-notes\v1.8.0.md -Commit 9030c5f -SigningThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    # The release-notes file of the version to release; asked for when omitted.
    [string]$NotesFile,
    # The commit to release (a hash); asked for when omitted.
    [string]$Commit,
    # Builds without running the fast tests first.
    [switch]$SkipTests,
    # The thumbprint of the code-signing certificate. Default: the environment variable DNNMANAGER_SIGNING_THUMBPRINT.
    [string]$SigningThumbprint = $env:DNNMANAGER_SIGNING_THUMBPRINT,
    # Only prints the release-notes files for VS Code's picker (Tasks Shell Input), one "value||label||description||detail"
    # per line. The commits come from publish-release.ps1 -List commits.
    [ValidateSet('notes')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$notesDir = Join-Path $root '.docs\release-notes'

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

# GitHub's REST API, signed in with the credential Git uses for github.com (never printed).
$script:token = $null
function Invoke-GitHub([string]$method, [string]$url, $body, [string]$inFile, [string]$contentType = 'application/json; charset=utf-8') {
    if (-not $script:token) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $env:GIT_TERMINAL_PROMPT = '0'
        try { $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null } finally { $ErrorActionPreference = $prev }
        $script:token = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
        if (-not $script:token) { throw 'No GitHub credential found - sign in to GitHub with Git first (e.g. git push once).' }
    }
    if ($url -notmatch '^https://') { $url = "https://api.github.com/repos/$script:repo$url" }
    $headers = @{ Authorization = "Bearer $script:token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $params = @{ Method = $method; Uri = $url; Headers = $headers; ContentType = $contentType; UseBasicParsing = $true }
    if ($null -ne $body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 5)) }
    if ($inFile) { $params.InFile = $inFile; $params.TimeoutSec = 1800 }
    return Invoke-RestMethod @params
}

function Test-GitHubRelease([string]$tag) {
    try { $null = Invoke-GitHub GET "/releases/tags/$tag"; return $true }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        if ($response -and $response.Value -and [int]$response.Value.StatusCode -eq 404) { return $false }
        throw
    }
}

# The notes files - the versions without an MSIX release first (the next one on top), then the released ones; newest first.
function Get-NotesFiles([string[]]$tags) {
    $all = @(Get-ChildItem $notesDir -Filter 'v*.md' | Where-Object { $_.BaseName -match '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' } |
            Sort-Object { [version](($_.BaseName.TrimStart('v')) -replace '-.*$', '') }, { $_.BaseName } -Descending)
    @($all | Where-Object { $tags -notcontains "msix-$($_.BaseName)" }) + @($all | Where-Object { $tags -contains "msix-$($_.BaseName)" })
}

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]

if ($List) {
    # Output for VS Code's picker: value||label||description||detail.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = Invoke-Git @('fetch', '--quiet', '--tags', 'origin')
    $tags = @(Invoke-Git @('tag', '--list', 'msix-v*'))
    $next = $true
    foreach ($f in Get-NotesFiles $tags) {
        $released = $tags -contains "msix-$($f.BaseName)"
        $state = if ($released) { 'MSIX already released' } elseif ($next) { 'next MSIX release' } else { 'no MSIX release' }
        if (-not $released) { $next = $false }
        $icon = if ($released) { '$(tag)' } else { '$(package)' }
        ".docs/release-notes/$($f.Name)||$icon $($f.BaseName) (MSIX)||$state||DnnManager_$($f.BaseName.TrimStart('v'))_x64.msix"
    }
    return
}

Write-Host "DNN Manager MSIX release - $script:repo" -ForegroundColor Cyan
Write-Host 'Fetching tags and branches from origin...'
$null = Invoke-Git @('fetch', '--quiet', '--tags', 'origin')
$tags = @(Invoke-Git @('tag', '--list', 'msix-v*'))

# --- 1. The version: the release-notes file ---

$files = @(Get-NotesFiles $tags)
if ($files.Count -eq 0) { throw "No vX.Y.Z.md files in $notesDir - write the release notes first." }
if ($NotesFile) {
    $path = if ([IO.Path]::IsPathRooted($NotesFile)) { $NotesFile } else { Join-Path $root $NotesFile }
    $notes = $files | Where-Object { $_.FullName -eq [IO.Path]::GetFullPath($path) } | Select-Object -First 1
    if (-not $notes) { throw "$NotesFile isn't a release-notes file (.docs\release-notes\vX.Y.Z.md)." }
}
else {
    $labels = $files | ForEach-Object { if ($tags -contains "msix-$($_.BaseName)") { "$($_.Name)  (MSIX already released)" } else { $_.Name } }
    $notes = $files[(Read-Choice 'Release notes (the file name is the version):' $labels 0)]
}

$version = $notes.BaseName.Substring(1)
$null = $version -match '^(\d+\.\d+\.\d+)(-.+)?$'
$fileVersion = "$($Matches[1]).0"
$prerelease = [bool]$Matches[2]
$tag = "msix-v$version"
$title = "v$version (MSIX)"
$msixName = "DnnManager_$($version)_x64.msix"

# --- 2. The commit ---

if ($Commit) {
    $sha = (Invoke-Git @('rev-parse', '--verify', "$Commit^{commit}")) | Select-Object -First 1
}
else {
    $branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1
    $ref = "origin/$branch"
    if (-not (Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/remotes/$ref"))) { $ref = 'origin/HEAD' }
    $log = @(Invoke-Git @('log', '-20', '--format=%H%x09%h  %cs  %s', $ref))
    if ($log.Count -eq 0) { throw 'No commits found on origin.' }
    $sha = ($log[(Read-Choice "Commit to release ($ref on GitHub, newest first):" ($log | ForEach-Object { ($_ -split "`t", 2)[1] }) 0)] -split "`t", 2)[0]
}
$subject = (Invoke-Git @('log', '-1', '--format=%h %s', $sha)) | Select-Object -First 1

# --- 3. Checks before building ---

Write-Step 'Checks'
if (-not $SigningThumbprint) {
    throw 'No signing certificate: give -SigningThumbprint, or set the environment variable DNNMANAGER_SIGNING_THUMBPRINT - see .docs\releasing.md, The MSIX package.'
}
$problems = @()
$localTag = if ($tags -contains $tag) { (Invoke-Git @('rev-parse', "refs/tags/$tag^{commit}")) | Select-Object -First 1 } else { $null }
if ($localTag -and $localTag -ne $sha) { $problems += "Tag $tag already exists on another commit ($($localTag.Substring(0, 7)))." }
if ((Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")) -and -not $localTag) { $problems += "Tag $tag exists on GitHub but not here - fetch it and check." }
if (Test-GitHubRelease $tag) { $problems += "GitHub already has a release $tag." }
if (@(Invoke-Git @('branch', '-r', '--contains', $sha)).Count -eq 0) { $problems += "$($subject.Substring(0, 7)) isn't on GitHub - push it first." }
if ($problems) { $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; throw 'Nothing was built or published.' }
Write-Host "  Version  $version$(if ($prerelease) { ' (pre-release)' }) - package $fileVersion"
Write-Host "  Notes    .docs\release-notes\$($notes.Name)"
Write-Host "  Commit   $subject"

# --- 4. Build in a worktree of that commit ---

$work = Join-Path $env:TEMP "dnnmanager-msix-$version"
$out = Join-Path $root "publish\$tag"
$msix = Join-Path $out $msixName
try {
    Write-Step "Building $msixName"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    $null = Invoke-Git @('worktree', 'prune')
    $null = Invoke-Git @('worktree', 'add', '--detach', $work, $sha)

    $package = Join-Path $work 'src\DnnManager.Package\build.ps1'
    # An older DNN Manager in a package would try to update itself into the package's read-only folder.
    if (-not (Test-Path $package)) { throw "This commit is older than the MSIX package (no src\DnnManager.Package) - release a newer one." }

    # The version goes into the manifest too (the exe's file properties), as publish-release.ps1 does.
    $appManifest = Join-Path $work 'app.manifest'
    [IO.File]::WriteAllText($appManifest, ([IO.File]::ReadAllText($appManifest) -replace '(<assemblyIdentity version=")[0-9.]+(")', "`${1}$fileVersion`${2}"), [Text.UTF8Encoding]::new($false))

    $tests = Join-Path $work 'tests\DnnManager.IntegrationTests'
    if ($SkipTests) { Write-Host '  Tests skipped (-SkipTests).' -ForegroundColor Yellow }
    elseif (Test-Path $tests) {
        Write-Step 'Fast tests'
        $null = Invoke-Tool dotnet @('test', $tests, '-c', 'Release', '--filter', 'TestCategory!=Integration', '--nologo')
    }

    $null = Invoke-Tool powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $package, '-Version', $version, '-CertificateThumbprint', $SigningThumbprint)

    # --- 5. Check the package ---
    Write-Step 'Package'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $null = New-Item -ItemType Directory -Force $out
    Copy-Item (Join-Path $work "publish\$msixName") $msix
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

    & (Join-Path $PSScriptRoot 'release-notes.ps1') -Version $version -Tag $tag -Repository $script:repo -OutFile (Join-Path $out 'release-notes.md')
}
finally {
    if (Test-Path $work) {
        try { $null = Invoke-Git @('worktree', 'remove', '--force', $work) }
        catch { Write-Host "  Couldn't remove the worktree $work - delete it and run git worktree prune." -ForegroundColor Yellow }
    }
}

# --- 6. Publish ---

Write-Step 'Ready to publish'
Write-Host "  Tag      $tag -> $subject (pushed to origin)"
Write-Host "  Release  $title$(if ($prerelease) { ' (pre-release)' }) - not the latest release: DNN Manager's own update and Setup don't see it"
Write-Host "  File     $msixName ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB) in $out"
if (-not (Confirm-Step "Publish $title on GitHub now?")) {
    Write-Host "Not published. The package stays in $out." -ForegroundColor Yellow
    return
}

Write-Step "Tag $tag"
if (-not $localTag) { $null = Invoke-Git @('tag', $tag, $sha) }
$null = Invoke-Tool git @('-C', $root, 'push', 'origin', "refs/tags/$tag")

Write-Step 'GitHub release'
$body = [IO.File]::ReadAllText((Join-Path $out 'release-notes.md'))
$release = Invoke-GitHub POST '/releases' @{ tag_name = $tag; name = $title; body = $body; draft = $true; prerelease = $prerelease; make_latest = 'false' }
Write-Host "  Draft created (id $($release.id)). Uploading $msixName..."
$asset = Invoke-GitHub POST "https://uploads.github.com/repos/$script:repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($msixName))" $null $msix 'application/octet-stream'
if ($asset.size -ne (Get-Item $msix).Length) { throw "$msixName arrived with $($asset.size) bytes, expected $((Get-Item $msix).Length). The draft release is left for you to check." }
# Never the latest: the latest release is the one DNN Manager and Setup update from, and it must have the exes.
$published = Invoke-GitHub PATCH "/releases/$($release.id)" @{ draft = $false; make_latest = 'false' }

Write-Host ''
Write-Host "Published $($published.html_url)" -ForegroundColor Green
