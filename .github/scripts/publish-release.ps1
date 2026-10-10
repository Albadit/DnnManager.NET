<#
.SYNOPSIS
    Starts a DNN Manager release from this PC: pick a release-notes file and a commit, and it checks them, runs the
    fast tests and pushes the version tag - GitHub builds, attests and publishes the release from that tag.

.DESCRIPTION
    1. Lists .docs\release-notes\vX.Y.Z.md - the file's name is the version, the tag and the release title.
    2. Lists the recent commits of the current branch; the newest is the default.
    3. Checks the tag is free and GitHub has no published release of it, and that the commit has the release notes
       and the release workflow (.github\workflows\release.yml) - the release is built from that commit, notes
       included.
    4. Checks out that commit into a temporary worktree (your working copy is not touched), restores the packages
       as GitHub does (locked mode: exactly packages.lock.json) and runs the fast tests - a problem shows here, before
       the tag exists.
    5. After you confirm: tags the commit and pushes the tag. Nothing built on this PC is uploaded: the release
       workflow builds the portable exe and the installer from the tag, signs the files
       when signing is set up, attests their provenance, tries the installed and the portable exe, puts them on a
       draft release, and publishes it only once all of that has passed and a reviewer has approved the publish job
       (a failure leaves at most a draft). This script then follows that run on GitHub (-NoWait doesn't).

    The only credential it uses is the one Git pushes the tag with. GitHub's public API is asked - without signing
    in - whether the release is published already and how the workflow run is going. The VS Code task
    "release (GitHub)" asks for the notes file and the commit in VS Code's pickers (filled by -List); run directly,
    it asks in the terminal.

.EXAMPLE
    .github\scripts\publish-release.ps1
.EXAMPLE
    .github\scripts\publish-release.ps1 -NotesFile .docs\release-notes\v1.7.0.md -Commit 9030c5f -SkipTests -NoWait
.EXAMPLE
    .github\scripts\publish-release.ps1 -List commits
#>
[CmdletBinding()]
param(
    # The release-notes file to release; asked for when omitted.
    [string]$NotesFile,
    # The commit to release (a hash); asked for when omitted.
    [string]$Commit,
    # Tags without running the fast tests here first (the release workflow runs none - CI tests the pushed commit).
    [switch]$SkipTests,
    # Pushes the tag and stops, without following the release workflow's run on GitHub.
    [switch]$NoWait,
    # Only prints the choices for VS Code's picker (Tasks Shell Input), one "value||label||description||detail" per line.
    [ValidateSet('notes', 'commits')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$notesDir = Join-Path $root '.docs\release-notes'

# --- Helpers (Invoke-Tool, Invoke-Git, Confirm-Step, Write-Step, Invoke-GitHub and Get-PublishedRelease are in
# ReleaseCommon.psm1, shared with redo-release.ps1) ---

Import-Module (Join-Path $PSScriptRoot 'ReleaseCommon.psm1') -Force
Set-ReleaseContext -Root $root

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

# --- The choices: release-notes files and the commits on GitHub ---

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]
Set-ReleaseContext -Repository $script:repo

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
$prerelease = [bool]$Matches[2]

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

# --- 3. Checks before tagging ---

Write-Step 'Checks'
$problems = @()
$remoteTag = Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")
$localTag = if ($tags -contains $tag) { (Invoke-Git @('rev-parse', "refs/tags/$tag^{commit}")) | Select-Object -First 1 } else { $null }
if ($localTag -and $localTag -ne $selected.Sha) { $problems += "Tag $tag already exists on another commit ($($localTag.Substring(0, 7)))." }
if ($remoteTag) { $problems += "Tag $tag is on GitHub already, so its release workflow has run - redo a draft with the task ""release: redo (GitHub)""." }
try {
    $published = Get-PublishedRelease $tag
    if ($published) { $problems += "GitHub has published $tag already ($($published.html_url)) - release the change as the next version." }
}
catch {
    # The release workflow refuses a published release anyway; this is only the early warning.
    Write-Host "  GitHub couldn't be asked whether $tag is published ($($_.Exception.Message)) - the release workflow checks it." -ForegroundColor Yellow
}
# The release is built from the commit: its notes (built into the exe, and the release's text) and the workflow that
# builds it must be in it.
foreach ($needed in ".docs/release-notes/$tag.md", '.github/workflows/release.yml') {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & git -C $root cat-file -e "$($selected.Sha):$needed" 2>$null
    $ErrorActionPreference = $prev
    if ($LASTEXITCODE -ne 0) { $problems += "$($selected.Short) has no $needed - commit it (and push), then release that commit." }
}
if ($problems) { $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }; throw 'Nothing was tagged or published.' }
Write-Host "  Tag $tag is free, and $($selected.Short) has its release notes and the release workflow."

$onRemote = @(Invoke-Git @('branch', '-r', '--contains', $selected.Sha))
if ($onRemote.Count -eq 0) {
    Write-Host "  $($selected.Short) isn't on GitHub yet (push $branch first to keep the branch in step)." -ForegroundColor Yellow
    if (-not (Confirm-Step '  Release it anyway? The tag push uploads the commit')) { throw 'Stopped - nothing was tagged or published.' }
}

Write-Host ''
Write-Host "  Version  $version$(if ($prerelease) { ' (pre-release)' })"
Write-Host "  Notes    .docs\release-notes\$($notes.Name)"
Write-Host "  Commit   $($selected.Short) $($selected.Subject)"
Write-Host "           $($selected.Author), $($selected.Date)"

# --- 4. The fast tests, in a worktree of that commit ---

if ($SkipTests) { Write-Host '  Fast tests skipped here (-SkipTests) - the release workflow runs none; CI tests the pushed commit.' -ForegroundColor Yellow }
else {
    $work = Join-Path $env:TEMP "dnnmanager-release-$version"
    try {
        Write-Step "Fast tests on $($selected.Short)"
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $null = Invoke-Git @('worktree', 'prune')
        $null = Invoke-Git @('worktree', 'add', '--detach', $work, $selected.Sha)
        $project = Join-Path $work 'tests\DnnManager.IntegrationTests\DnnManager.IntegrationTests.csproj'
        # The packages packages.lock.json names, from nuget.org only (nuget.config), restored as the release workflow
        # restores them: a lock file that doesn't match fails here, before anything is tagged.
        $null = Invoke-Tool dotnet @('restore', $project, '--locked-mode')
        # The launcher's too (the Native AOT compiler, which comes with the SDK global.json names).
        $null = Invoke-Tool dotnet @('restore', (Join-Path $work 'src\DnnManager.Launcher\DnnManager.Launcher.csproj'), '--locked-mode')
        $null = Invoke-Tool dotnet @('test', $project, '-c', 'Release', '--no-restore', '--filter', 'TestCategory!=Integration', '--nologo')
    }
    finally {
        if (Test-Path $work) {
            try { $null = Invoke-Git @('worktree', 'remove', '--force', $work) }
            catch { Write-Host "  Couldn't remove the worktree $work - delete it and run git worktree prune." -ForegroundColor Yellow }
        }
    }
}

# --- 5. The tag: GitHub builds and publishes the release from it ---

Write-Step 'Ready to release'
Write-Host "  Tag      $tag -> $($selected.Short), pushed to origin"
Write-Host "  Then     GitHub's release workflow builds both exes from it, runs every test, attests them and publishes"
Write-Host "           $tag$(if ($prerelease) { ' as a pre-release' } else { ' as the latest release' }) with .docs\release-notes\$($notes.Name)"
if (-not (Confirm-Step "Push the tag $tag now?")) {
    Write-Host 'Nothing was tagged.' -ForegroundColor Yellow
    return
}

Write-Step "Tag $tag"
if (-not $localTag) { $null = Invoke-Git @('tag', $tag, $selected.Sha) }
$null = Invoke-Tool git @('-C', $root, 'push', 'origin', "refs/tags/$tag")

$actions = "https://github.com/$script:repo/actions/workflows/release.yml"
if ($NoWait) {
    Write-Host ''
    Write-Host "Tag pushed. The release workflow builds and publishes $tag - follow it at $actions" -ForegroundColor Green
    return
}

# --- 6. Follow the release workflow on GitHub ---

Write-Step "Release workflow on $tag"
$deadline = (Get-Date).AddMinutes(90)
while ($true) {
    try {
        $runs = @((Invoke-GitHub "/actions/workflows/release.yml/runs?head_sha=$($selected.Sha)&event=push&per_page=10").workflow_runs |
                Where-Object { $_.head_branch -eq $tag })
        $run = $runs | Sort-Object { [datetime]$_.created_at } -Descending | Select-Object -First 1
    }
    catch {
        Write-Host "  GitHub couldn't be asked ($($_.Exception.Message)) - follow the run at $actions" -ForegroundColor Yellow
        return
    }
    if ($run -and $run.status -eq 'completed') {
        if ($run.conclusion -eq 'success') { break }
        throw "The release workflow on $tag ended '$($run.conclusion)': $($run.html_url) - nothing was published (at most a draft is left). Fix it and redo the release (""release: redo (GitHub)""), or release the next version."
    }
    if ((Get-Date) -gt $deadline) {
        Write-Host "  Not finished within 90 minutes - follow it at $(if ($run) { $run.html_url } else { $actions })" -ForegroundColor Yellow
        return
    }
    # The draft is ready and the publish job waits for a reviewer (the environment "publish").
    if ($run -and $run.status -eq 'waiting') {
        Write-Host "  The draft is ready - approve the job 'Publish the release' at $($run.html_url) to publish it." -ForegroundColor Yellow
    }
    Write-Host ("  {0} - checking again in 2 minutes..." -f $(if ($run) { "The run is $($run.status)" } else { 'The run has not started yet' }))
    Start-Sleep -Seconds 120
}

$release = $null
try { $release = Get-PublishedRelease $tag } catch { $release = $null }
Write-Host ''
Write-Host "Published $(if ($release) { $release.html_url } else { "https://github.com/$script:repo/releases/tag/$tag" })" -ForegroundColor Green
