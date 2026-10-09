<#
.SYNOPSIS
    Publishes a DNN Manager release from this PC: pick a release-notes file and a commit, and it builds, tags and
    publishes the GitHub release.

.DESCRIPTION
    1. Lists .docs\release-notes\vX.Y.Z.md - the file's name is the version, the tag and the release title.
    2. Lists the recent commits of the current branch; the newest is the default.
    3. Checks out that commit into a temporary worktree (your working copy is not touched), restores the packages
       as CI does (locked mode: exactly packages.lock.json), runs the fast tests, and builds the portable exe and the installer with the version stamped in. (GitHub Actions only builds and
       tests the pushed tag - .github\workflows\ci.yml.)
    4. Checks both files report the version. They land in publish\vX.Y.Z.
    5. After you confirm: tags the commit, pushes the tag, creates the GitHub release with the notes as a draft,
       uploads the files (and SHA256SUMS.txt), checks each against GitHub's own SHA-256 of it, waits for the CI run
       on the tag to pass, and only then publishes it - every DNN Manager is offered a release CI has tested. A
       failed CI run leaves the draft, to look into.

    It signs in to GitHub with the credential Git already uses for this repository. The VS Code task
    "release (GitHub)" asks for the notes file and the commit in VS Code's pickers (filled by -List); run directly,
    it asks in the terminal.

.EXAMPLE
    .github\scripts\publish-release.ps1
.EXAMPLE
    .github\scripts\publish-release.ps1 -NotesFile .docs\release-notes\v1.7.0.md -Commit 9030c5f -SkipTests
.EXAMPLE
    .github\scripts\publish-release.ps1 -List commits
#>
[CmdletBinding()]
param(
    # The release-notes file to release; asked for when omitted.
    [string]$NotesFile,
    # The commit to release (a hash); asked for when omitted.
    [string]$Commit,
    # Builds without running the fast tests first - a pre-release only.
    [switch]$SkipTests,
    # Publishes without waiting for CI on the tag - a pre-release only.
    [switch]$SkipCi,
    # Only prints the choices for VS Code's picker (Tasks Shell Input), one "value||label||description||detail" per line.
    [ValidateSet('notes', 'commits')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$notesDir = Join-Path $root '.docs\release-notes'

# --- Helpers ---

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

# --- The choices: release-notes files and the commits on GitHub ---

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]

# The notes files - the not yet tagged ones first (the next release on top), then the released ones; newest first.
function Get-NotesFiles([string[]]$tags) {
    $all = @(Get-ChildItem $notesDir -Filter 'v*.md' | Where-Object { $_.BaseName -match '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' } |
            Sort-Object { [version](($_.BaseName.TrimStart('v')) -replace '-.*$', '') }, { $_.BaseName } -Descending)
    @($all | Where-Object { $tags -notcontains $_.BaseName }) + @($all | Where-Object { $tags -contains $_.BaseName })
}

# The 20 newest commits of the current branch as GitHub has it (origin/<branch>, or origin's default branch).
function Get-GitHubCommits {
    $branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1
    $ref = "origin/$branch"
    if (-not (Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/remotes/$ref"))) { $ref = 'origin/HEAD' }
    $log = @(Invoke-Git @('log', '-20', '--format=%H%x09%h%x09%cs%x09%an%x09%s', $ref))
    @($log | ForEach-Object {
            $f = $_ -split "`t", 5
            [pscustomobject]@{ Sha = $f[0]; Short = $f[1]; Date = $f[2]; Author = $f[3]; Subject = $f[4]; Ref = $ref }
        })
}

# First lines of a notes file: its bold summary, for the picker.
function Get-NotesSummary([IO.FileInfo]$file) {
    $line = [IO.File]::ReadAllLines($file.FullName) | Where-Object { $_ -and -not $_.StartsWith('#') } | Select-Object -First 1
    if (-not $line) { return '' }
    $line = $line -replace '\*\*', '' -replace '`', ''
    if ($line.Length -gt 110) { $line = $line.Substring(0, 107) + '...' }
    return $line
}

if ($List) {
    # Output for VS Code's picker: value||label||description||detail.
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = Invoke-Git @('fetch', '--quiet', '--tags', 'origin')
    $tags = @(Invoke-Git @('tag', '--list', 'v*'))
    $clean = { param($s) ($s -replace '\|\|', '|').Trim() }
    if ($List -eq 'notes') {
        $next = $true
        foreach ($f in Get-NotesFiles $tags) {
            $released = $tags -contains $f.BaseName
            $state = if ($released) { 'already released' } elseif ($next) { 'next release' } else { 'not released' }
            if (-not $released) { $next = $false }
            $icon = if ($released) { '$(tag)' } else { '$(rocket)' }
            ".docs/release-notes/$($f.Name)||$icon $($f.BaseName)||$state||$(& $clean (Get-NotesSummary $f))"
        }
    }
    else {
        foreach ($c in Get-GitHubCommits) {
            "$($c.Sha)||`$(git-commit) $($c.Short)  $(& $clean $c.Subject)||$($c.Date), $(& $clean $c.Author)||on GitHub ($($c.Ref))"
        }
    }
    return
}

Write-Host "DNN Manager release - $script:repo" -ForegroundColor Cyan
Write-Host 'Fetching tags and branches from origin...'
$null = Invoke-Git @('fetch', '--quiet', '--tags', 'origin')
$tags = @(Invoke-Git @('tag', '--list', 'v*'))

# --- 1. The release-notes file: its name is the version, the tag and the title ---

$files = @(Get-NotesFiles $tags)
if ($files.Count -eq 0) { throw "No vX.Y.Z.md files in $notesDir - write the release notes first." }
if ($NotesFile) {
    $path = if ([IO.Path]::IsPathRooted($NotesFile)) { $NotesFile } else { Join-Path $root $NotesFile }
    $notes = $files | Where-Object { $_.FullName -eq [IO.Path]::GetFullPath($path) } | Select-Object -First 1
    if (-not $notes) { throw "$NotesFile isn't a release-notes file (.docs\release-notes\vX.Y.Z.md)." }
}
else {
    $labels = $files | ForEach-Object { if ($tags -contains $_.BaseName) { "$($_.Name)  (already tagged)" } else { $_.Name } }
    $notes = $files[(Read-Choice 'Release notes (the file name is the tag and the title):' $labels 0)]
}

$tag = $notes.BaseName
$version = $tag.Substring(1)
$null = $version -match '^(\d+\.\d+\.\d+)(-.+)?$'
$fileVersion = "$($Matches[1]).0"
$prerelease = [bool]$Matches[2]
# A release every DNN Manager is offered as an update is tested - here, and by CI on its tag before it is published.
if (-not $prerelease -and ($SkipTests -or $SkipCi)) { throw '-SkipTests and -SkipCi are for a pre-release only (vX.Y.Z-something).' }

# --- 2. The commit ---

if ($Commit) {
    $sha = (Invoke-Git @('rev-parse', '--verify', "$Commit^{commit}")) | Select-Object -First 1
    $f = ((Invoke-Git @('log', '-1', '--format=%H%x09%h%x09%cs%x09%an%x09%s', $sha)) | Select-Object -First 1) -split "`t", 5
    $selected = [pscustomobject]@{ Sha = $f[0]; Short = $f[1]; Date = $f[2]; Author = $f[3]; Subject = $f[4] }
}
else {
    $commits = @(Get-GitHubCommits)
    if ($commits.Count -eq 0) { throw 'No commits found on origin.' }
    $commitLabels = $commits | ForEach-Object { $s = if ($_.Subject.Length -gt 70) { $_.Subject.Substring(0, 67) + '...' } else { $_.Subject }; "$($_.Short)  $($_.Date)  $s" }
    $selected = $commits[(Read-Choice "Commit to release ($($commits[0].Ref) on GitHub, newest first):" $commitLabels 0)]
}
$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1

# --- 3. Checks before building ---

Write-Step 'Checks'
$problems = @()
$remoteTag = Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")
$localTag = if ($tags -contains $tag) { (Invoke-Git @('rev-parse', "refs/tags/$tag^{commit}")) | Select-Object -First 1 } else { $null }
if ($localTag -and $localTag -ne $selected.Sha) { $problems += "Tag $tag already exists on another commit ($($localTag.Substring(0, 7)))." }
if ($remoteTag -and -not $localTag) { $problems += "Tag $tag exists on GitHub but not here - fetch it and check." }
if (Test-GitHubRelease $tag) { $problems += "GitHub already has a release $tag." }
if ($problems) { $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; throw 'Nothing was built or published.' }
Write-Host "  Tag $tag and its GitHub release are free."

$onRemote = @(Invoke-Git @('branch', '-r', '--contains', $selected.Sha))
if ($onRemote.Count -eq 0) {
    Write-Host "  $($selected.Short) isn't on GitHub yet (push $branch first to keep the branch in step)." -ForegroundColor Yellow
    if (-not (Confirm-Step '  Release it anyway? The tag push uploads the commit')) { throw 'Stopped - nothing was built or published.' }
}

Write-Host ''
Write-Host "  Version  $version$(if ($prerelease) { ' (pre-release)' })"
Write-Host "  Notes    .docs\release-notes\$($notes.Name)"
Write-Host "  Commit   $($selected.Short) $($selected.Subject)"
Write-Host "           $($selected.Author), $($selected.Date)"

# --- 4. Build in a worktree of that commit ---

$work = Join-Path $env:TEMP "dnnmanager-release-$version"
$out = Join-Path $root "publish\$tag"
try {
    Write-Step "Building $tag from $($selected.Short)"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    $null = Invoke-Git @('worktree', 'prune')
    $null = Invoke-Git @('worktree', 'add', '--detach', $work, $selected.Sha)

    $installer = Join-Path $work 'src\DnnManager.Installer\build.ps1'
    if (-not (Test-Path $installer) -or -not (Select-String -Path $installer -Pattern '\[string\]\$Version' -Quiet)) {
        throw "This commit's installer script has no -Version parameter - release a newer commit."
    }
    # Reuse the Inno Setup this repository already downloaded - build.ps1 checks it against its pinned SHA-512.
    $inno = Join-Path $root 'src\DnnManager.Installer\bin\tools'
    if (Test-Path $inno) { Copy-Item $inno (Join-Path $work 'src\DnnManager.Installer\bin\tools') -Recurse -Force }

    # The version goes into the manifest too (the exe's file properties).
    $manifest = Join-Path $work 'app.manifest'
    [IO.File]::WriteAllText($manifest, ([IO.File]::ReadAllText($manifest) -replace '(<assemblyIdentity version=")[0-9.]+(")', "`${1}$fileVersion`${2}"), [Text.UTF8Encoding]::new($false))

    # The packages packages.lock.json names, restored as CI restores them: a lock file that doesn't match fails here,
    # before anything is tagged - not in the CI run the release then waits for. The tests, the portable exe and the
    # installer below restore the same way (MSBuild reads the environment variable as the property).
    $tests = Join-Path $work 'tests\DnnManager.IntegrationTests'
    $env:RestoreLockedMode = 'true'
    if (Test-Path (Join-Path $tests 'packages.lock.json')) {
        Write-Step 'Packages'
        $null = Invoke-Tool dotnet @('restore', (Join-Path $tests 'DnnManager.IntegrationTests.csproj'), '--locked-mode')
    }

    if ($SkipTests) { Write-Host '  Tests skipped (-SkipTests).' -ForegroundColor Yellow }
    elseif (Test-Path $tests) {
        Write-Step 'Fast tests'
        $null = Invoke-Tool dotnet @('test', $tests, '-c', 'Release', '--filter', 'TestCategory!=Integration', '--nologo')
    }

    $stamp = @("-p:Version=$version", "-p:AssemblyVersion=$fileVersion", "-p:FileVersion=$fileVersion")
    Write-Step 'Portable exe'
    $null = Invoke-Tool dotnet (@('publish', (Join-Path $work 'DnnManager.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
            '-p:PortableExe=true', '--nologo', '-o', (Join-Path $work 'publish')) + $stamp)

    Write-Step 'Installer'
    # The pinned Inno Setup, never whichever is installed on this PC: every release is compiled by the same one.
    $null = Invoke-Tool powershell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Version', $version, '-PinnedInno')

    # --- 5. Collect and check the files ---
    Write-Step 'Files'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    $null = New-Item -ItemType Directory -Force $out
    $assets = "DnnManager_Portable-$version-x64.exe", "DnnManager_Setup-$version-x64.exe"
    foreach ($name in $assets) { Copy-Item (Join-Path $work "publish\$name") $out }
    foreach ($name in $assets) {
        $info = (Get-Item (Join-Path $out $name)).VersionInfo
        $product = ($info.ProductVersion -split '\+')[0].Trim()
        Write-Host ("  {0}: file version {1}, product version {2}" -f $name, $info.FileVersion.Trim(), $info.ProductVersion.Trim())
        if ($product -ne $version) { throw "$name reports $product, not $version." }
    }
    # The files' SHA-256, for whoever downloads them by hand: a file beside them, and in the release's notes.
    $sums = foreach ($name in $assets) { "{0}  {1}" -f (Get-FileHash (Join-Path $out $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name }
    [IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), ($sums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $sums | ForEach-Object { Write-Host "  $_" }

    & (Join-Path $PSScriptRoot 'release-notes.ps1') -Version $version -Tag $tag -Repository $script:repo -OutFile (Join-Path $out 'release-notes.md')
}
finally {
    Remove-Item Env:RestoreLockedMode -ErrorAction SilentlyContinue
    if (Test-Path $work) {
        try { $null = Invoke-Git @('worktree', 'remove', '--force', $work) }
        catch { Write-Host "  Couldn't remove the worktree $work - delete it and run git worktree prune." -ForegroundColor Yellow }
    }
}

# --- 6. Publish ---

Write-Step 'Ready to publish'
Write-Host "  Tag      $tag -> $($selected.Short) (pushed to origin)"
Write-Host "  Release  $tag$(if ($prerelease) { ' (pre-release)' } else { ' (latest)' }), notes from .docs\release-notes\$($notes.Name)"
Write-Host "  Files    $($assets -join ', ')"
Write-Host "           in $out"
if (-not (Confirm-Step "Publish $tag on GitHub now?")) {
    Write-Host "Not published. The files stay in $out." -ForegroundColor Yellow
    return
}

Write-Step "Tag $tag"
if (-not $localTag) { $null = Invoke-Git @('tag', $tag, $selected.Sha) }
$null = Invoke-Tool git @('-C', $root, 'push', 'origin', "refs/tags/$tag")

Write-Step 'GitHub release'
$sumsText = [IO.File]::ReadAllText((Join-Path $out 'SHA256SUMS.txt')).Trim()
$body = [IO.File]::ReadAllText((Join-Path $out 'release-notes.md')).TrimEnd() + "`n`n**SHA-256**`n`n``````text`n$sumsText`n```````n"
$release = Invoke-GitHub POST '/releases' @{ tag_name = $tag; name = $tag; body = $body; draft = $true; prerelease = $prerelease }
Write-Host "  Draft created (id $($release.id))."
foreach ($name in $assets + 'SHA256SUMS.txt') {
    $path = Join-Path $out $name
    Write-Host "  Uploading $name ($([math]::Round((Get-Item $path).Length / 1MB, 1)) MB)..."
    $asset = Invoke-GitHub POST "https://uploads.github.com/repos/$script:repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))" $null $path 'application/octet-stream'
    if ($asset.size -ne (Get-Item $path).Length) { throw "$name arrived with $($asset.size) bytes, expected $((Get-Item $path).Length). The draft release is left for you to check." }
    # GitHub's own SHA-256 of what it stored is what every DNN Manager checks its download against: it must be this file's.
    $local = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $digest = ''
    for ($try = 0; $try -lt 5 -and -not $digest; $try++) {
        if ($try -gt 0) { Start-Sleep -Seconds 2; $asset = Invoke-GitHub GET "/releases/assets/$($asset.id)" }
        $property = $asset.PSObject.Properties['digest']
        if ($property -and $property.Value) { $digest = "$($property.Value)" }
    }
    if ($digest -ne "sha256:$local") { throw "$name : GitHub lists $(if ($digest) { $digest } else { 'no SHA-256' }), not sha256:$local. The draft release is left for you to check." }
}

# CI on the tag (pushed above) builds it again and runs the whole test suite: the release becomes the one every DNN
# Manager is offered only once that has passed.
if ($SkipCi) { Write-Host '  Not waiting for CI (-SkipCi, a pre-release).' -ForegroundColor Yellow }
else {
    Write-Step "Waiting for CI on $tag"
    $deadline = (Get-Date).AddMinutes(90)
    while ($true) {
        $runs = @((Invoke-GitHub GET "/actions/runs?head_sha=$($selected.Sha)&event=push&per_page=20").workflow_runs |
                Where-Object { $_.name -eq 'CI' -and $_.head_branch -eq $tag })
        $run = $runs | Sort-Object { [datetime]$_.created_at } -Descending | Select-Object -First 1
        if ($run -and $run.status -eq 'completed') {
            if ($run.conclusion -eq 'success') { Write-Host "  CI passed: $($run.html_url)" -ForegroundColor Green; break }
            throw "CI on $tag ended '$($run.conclusion)': $($run.html_url) - the release stays a draft. Fix it and release the next version, or run the release again with redo once CI passes."
        }
        if ((Get-Date) -gt $deadline) { throw "CI on $tag hasn't finished within 90 minutes - the release stays a draft; publish it on GitHub once CI has passed." }
        Write-Host ("  {0} - checking again in 30 seconds..." -f $(if ($run) { "CI is $($run.status)" } else { 'CI has not started yet' }))
        Start-Sleep -Seconds 30
    }
}
$published = Invoke-GitHub PATCH "/releases/$($release.id)" @{ draft = $false; make_latest = $(if ($prerelease) { 'false' } else { 'true' }) }

Write-Host ''
Write-Host "Published $($published.html_url)" -ForegroundColor Green
