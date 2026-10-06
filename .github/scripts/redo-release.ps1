<#
.SYNOPSIS
    Redoes a release whose tag is already made: folds your changes into the release commit, removes the old tag and
    GitHub release, and releases that version again.

.DESCRIPTION
    For a release that failed (the workflow's tests, say) or that needs one more change before anyone has it.
    1. The version: -Version, or the newest version tag.
    2. The release commit is the tag's commit - it must be the newest commit of the current branch (or, with no tag
       yet, the newest commit when its message starts with "release: vX.Y.Z").
    3. Shows what it will do and asks once:
       - amends the release commit with every change in your working copy (new files too), with the same message
         or the one you type;
       - pushes the branch (--force-with-lease when the old commit was on GitHub already);
       - deletes the GitHub release of that tag, if there is one - a published one only after you type its tag,
         since DNN Manager may have offered it as an update already;
       - deletes the tag here and on GitHub.
    4. Then releases it again, if you want: builds and publishes it from this PC (publish-release.ps1, the task
       "release (GitHub)"), which makes the tag again.

    It signs in to GitHub with the credential Git already uses for this repository. -DryRun shows the plan and
    changes nothing.

.EXAMPLE
    .github\scripts\redo-release.ps1
.EXAMPLE
    .github\scripts\redo-release.ps1 -Version 1.7.6 -Message "release: v1.7.6 - keep running in the background"
.EXAMPLE
    .github\scripts\redo-release.ps1 -DryRun
#>
[CmdletBinding()]
param(
    # The version to redo (1.7.6 or v1.7.6); the newest version tag when omitted.
    [string]$Version,
    # The release commit's new message; asked for when omitted (Enter keeps the current one).
    [string]$Message,
    # Passed on to publish-release.ps1: builds without running the fast tests first.
    [switch]$SkipTests,
    # Shows what would be done, and does nothing.
    [switch]$DryRun
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

function Confirm-Step([string]$question) {
    $answer = Read-Host "$question [y/N]"
    return $answer -match '^(y|yes)$'
}

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

# GitHub's REST API, signed in with the credential Git uses for github.com (never printed).
$script:token = $null
function Invoke-GitHub([string]$method, [string]$url) {
    if (-not $script:token) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $env:GIT_TERMINAL_PROMPT = '0'
        try { $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null } finally { $ErrorActionPreference = $prev }
        $script:token = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
        if (-not $script:token) { throw 'No GitHub credential found - sign in to GitHub with Git first (e.g. git push once).' }
    }
    if ($url -notmatch '^https://') { $url = "https://api.github.com/repos/$script:repo$url" }
    $headers = @{ Authorization = "Bearer $script:token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    return Invoke-RestMethod -Method $method -Uri $url -Headers $headers -UseBasicParsing
}

# --- 1. The version and its tag ---

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]

Write-Host "Redo a DNN Manager release - $script:repo" -ForegroundColor Cyan
Write-Host 'Fetching tags and branches from origin...'
# Branches only: the tags are compared with GitHub's below, never overwritten.
$null = Invoke-Git @('fetch', '--quiet', '--no-tags', 'origin')

if (-not $Version) {
    $Version = (Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*')) | Select-Object -First 1
    if (-not $Version) { throw 'No version tag found - give -Version.' }
}
$tag = 'v' + $Version.TrimStart('v', 'V')
if ($tag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "$Version isn't a version like 1.7.6." }
$notes = Join-Path $root ".docs\release-notes\$tag.md"
if (-not (Test-Path $notes)) { throw "There are no release notes .docs\release-notes\$tag.md - the release needs them." }

$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1
if ($branch -eq 'HEAD') { throw 'No branch is checked out (detached HEAD) - check out the branch the release is on.' }
$head = (Invoke-Git @('rev-parse', 'HEAD')) | Select-Object -First 1
$headSubject = (Invoke-Git @('log', '-1', '--format=%s', 'HEAD')) | Select-Object -First 1

$localTag = (Invoke-Git @('tag', '--list', $tag)) | Select-Object -First 1
$localTagSha = if ($localTag) { (Invoke-Git @('rev-parse', "refs/tags/$tag^{commit}")) | Select-Object -First 1 } else { $null }
# An annotated tag is listed twice - the tag, then (^{}) the commit it points at.
$remoteTagLines = @(Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag"))
$remoteTagLine = @($remoteTagLines | Where-Object { $_ -like '*^{}' }) + @($remoteTagLines) | Select-Object -First 1
$remoteTagSha = if ($remoteTagLine) { ($remoteTagLine -split "`t")[0] } else { $null }
if ($localTagSha -and $remoteTagSha -and $localTagSha -ne $remoteTagSha) {
    throw "Tag $tag points at $($localTagSha.Substring(0, 7)) here but at $($remoteTagSha.Substring(0, 7)) on GitHub - sort that out first."
}

# --- 2. The release commit: the tag's, which must be the newest of the branch ---

$releaseSha = if ($localTagSha) { $localTagSha } elseif ($remoteTagSha) { $remoteTagSha } else { $null }
if (-not $releaseSha) {
    if ($headSubject -notlike "release: $tag*") {
        throw "There is no tag $tag, and the newest commit isn't its release commit (""$headSubject"")."
    }
    $releaseSha = $head
}
if ($releaseSha -ne $head) {
    $after = @(Invoke-Git @('log', '--format=%h %s', "$releaseSha..HEAD"))
    if ($after.Count -eq 0) { throw "Tag $tag points at $($releaseSha.Substring(0, 7)), which isn't on $branch." }
    Write-Host "Tag $tag points at $($releaseSha.Substring(0, 7)), and $branch has newer commits:" -ForegroundColor Red
    $after | ForEach-Object { Write-Host "  $_" }
    throw 'Only the newest commit can be rewritten here. Fold those commits into it first (or release them as the next version).'
}
$releaseSubject = $headSubject
$releaseBody = @(Invoke-Git @('log', '-1', '--format=%b', 'HEAD')) -join "`n"
if ($releaseBody.Trim()) { Write-Host '  The release commit has a body; a new message replaces it, keeping the message keeps it.' -ForegroundColor DarkGray }

# GitHub's branch: the release commit itself (then it is replaced with --force-with-lease, which fails if GitHub moved
# on meanwhile), or a commit before it. Anything else would be lost by the push.
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$remoteBranchSha = (& git -C $root rev-parse --verify --quiet "refs/remotes/origin/$branch" 2>$null) | Select-Object -First 1
$ErrorActionPreference = $prev
$pushedAlready = [bool]$remoteBranchSha -and $remoteBranchSha -eq $head
if ($remoteBranchSha -and -not $pushedAlready) {
    & git -C $root merge-base --is-ancestor $remoteBranchSha $head
    if ($LASTEXITCODE -ne 0) {
        throw "origin/$branch ($($remoteBranchSha.Substring(0, 7))) has commits that $branch hasn't - pull first. Nothing was changed."
    }
}

$changes = @(Invoke-Git @('status', '--porcelain', '--untracked-files=all'))

# --- 3. The GitHub release of that tag (drafts aren't found by tag, so the list is read) ---

$release = $null
$releaseChecked = $true
try {
    # Kept in a variable first: Windows PowerShell 5.1 passes a JSON array on as one object, and @(...) around the
    # call would make every release one item - whose tag_name "matches" when any release has the tag.
    $all = Invoke-GitHub GET '/releases?per_page=100'
    $found = @(foreach ($r in $all) { if ([string]$r.tag_name -eq $tag) { $r } })
    if ($found.Count -gt 1) { throw "GitHub has $($found.Count) releases with the tag $tag - delete the extra ones by hand." }
    if ($found.Count -eq 1) {
        $release = $found[0]
        if (@($release.id).Count -ne 1) { throw "GitHub's answer for the release $tag couldn't be read." }
    }
}
catch {
    # A dry run still shows the rest of the plan.
    if (-not $DryRun) { throw }
    $releaseChecked = $false
    Write-Host "  GitHub's releases couldn't be read: $($_.Exception.Message)" -ForegroundColor Yellow
}

if (-not $Message -and -not $DryRun) {
    Write-Host ''
    Write-Host "Release commit message (Enter keeps it):" -ForegroundColor Cyan
    Write-Host "  $releaseSubject"
    $typed = Read-Host 'New message'
    if ($typed.Trim()) { $Message = $typed.Trim() }
}
$newMessage = if ($Message) { $Message } else { $null }

if ($changes.Count -eq 0 -and -not $newMessage -and -not $localTagSha -and -not $remoteTagSha -and -not $release) {
    Write-Host 'Nothing to redo: no changes, no new message, no tag and no GitHub release.' -ForegroundColor Yellow
    return
}

# --- 4. The plan ---

Write-Step "Redo $tag"
if ($changes.Count -gt 0 -or $newMessage) {
    Write-Host "  Amend    $($head.Substring(0, 7)) ""$releaseSubject"""
    if ($changes.Count -gt 0) {
        Write-Host "           with $($changes.Count) changed file(s):"
        $changes | Select-Object -First 25 | ForEach-Object { Write-Host "             $_" }
        if ($changes.Count -gt 25) { Write-Host "             ... and $($changes.Count - 25) more" }
    }
    if ($newMessage) { Write-Host "           new message: ""$newMessage""" }
    if ($pushedAlready) { Write-Host "  Push     $branch with --force-with-lease (it replaces $($head.Substring(0, 7)) on GitHub)" }
    else { Write-Host "  Push     $branch" }
}
else {
    Write-Host "  Commit   $($head.Substring(0, 7)) ""$releaseSubject"" stays as it is"
}
if ($release) {
    $state = if ($release.draft) { 'draft' } else { 'PUBLISHED' }
    Write-Host "  Delete   the GitHub release $tag ($state, $(@($release.assets).Count) file(s))"
    if (-not $release.draft) {
        Write-Host '           It is published: running DNN Managers may have offered it as an update already.' -ForegroundColor Yellow
    }
}
if (-not $releaseChecked) { Write-Host "  Delete   the GitHub release $tag, if there is one (not checked in this dry run)" }
if ($remoteTagSha) { Write-Host "  Delete   the tag $tag on GitHub" }
if ($localTagSha) { Write-Host "  Delete   the tag $tag here" }
Write-Host '  Then     release it again from this PC - asked at the end'

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run - nothing was changed.' -ForegroundColor Yellow
    return
}

Write-Host ''
if (-not (Confirm-Step "Redo $tag like this?")) { Write-Host 'Nothing was changed.' -ForegroundColor Yellow; return }
if ($release -and -not $release.draft) {
    $typed = Read-Host "Type $tag to delete its published release"
    if ($typed -ne $tag) { Write-Host 'Nothing was changed.' -ForegroundColor Yellow; return }
}

# --- 5. Do it: the commit and the branch first (nothing on GitHub is gone if they fail), then the release and tag ---

if ($changes.Count -gt 0 -or $newMessage) {
    Write-Step 'Commit'
    if ($changes.Count -gt 0) { $null = Invoke-Git @('add', '--all') }
    $amend = @('-C', $root, 'commit', '--amend')
    if ($newMessage) { $amend += @('-m', $newMessage) } else { $amend += '--no-edit' }
    Invoke-Tool git $amend
    $head = (Invoke-Git @('rev-parse', 'HEAD')) | Select-Object -First 1

    Write-Step "Push $branch"
    if ($pushedAlready) { Invoke-Tool git @('-C', $root, 'push', "--force-with-lease=$branch`:$remoteBranchSha", 'origin', $branch) }
    else { Invoke-Tool git @('-C', $root, 'push', 'origin', $branch) }
}

if ($release) {
    Write-Step "Delete the GitHub release $tag"
    $null = Invoke-GitHub DELETE "/releases/$($release.id)"
    Write-Host '  Deleted.'
}
if ($remoteTagSha -or $localTagSha) {
    Write-Step "Delete the tag $tag"
    if ($remoteTagSha) { Invoke-Tool git @('-C', $root, 'push', 'origin', ":refs/tags/$tag") }
    if ($localTagSha) { Invoke-Tool git @('-C', $root, 'tag', '-d', $tag) }
}

# --- 6. Release it again ---

$short = $head.Substring(0, 7)
Write-Step "Release $tag from $short"
if (Confirm-Step "Build and publish $tag from this PC now (publish-release.ps1 - the fast tests, both exes, the tag, the release)?") {
    $publishArgs = @{ NotesFile = ".docs\release-notes\$tag.md"; Commit = $head }
    if ($SkipTests) { $publishArgs.SkipTests = $true }
    & (Join-Path $PSScriptRoot 'publish-release.ps1') @publishArgs
}
else {
    Write-Host ''
    Write-Host "Not released. $short is pushed without a tag - release it with the task ""release (GitHub)"", or run this again." -ForegroundColor Yellow
}
