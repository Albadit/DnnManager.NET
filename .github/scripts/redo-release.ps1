<#
.SYNOPSIS
    Redoes a release whose tag is made but which isn't published: folds your changes into the release commit,
    removes the old tag and releases that version again.

.DESCRIPTION
    For a release whose workflow failed (the tests, say) or that needs one more change before it is published. A
    published release is never redone - not even a pre-release: running DNN Managers may have installed it, and with
    immutable releases its tag can't move. Release the change as the next version instead.
    1. The version: -Version, or the newest version tag.
    2. Refuses when GitHub has published that release, or while a release workflow run on its tag hasn't ended
       (queued, running, or waiting for the publish job's approval) - asked without signing in; refused too when
       GitHub can't be asked.
    3. The release commit is the tag's commit - it must be the newest commit of the current branch (or, with no tag
       yet, the newest commit when its message starts with "release: vX.Y.Z").
    4. Shows what it will do and asks once:
       - amends the release commit with the changes in your working copy to files git has (git add -u) - new files
         only when you say yes to them, listed first - with the same message or the one you type;
       - pushes the branch (--force-with-lease when the old commit was on GitHub already);
       - deletes the tag here and on GitHub.
       A draft release the failed run left is kept: the next release workflow run on the tag reuses it, replacing
       its files and notes.
    5. Then releases it again, if you want: publish-release.ps1 (the task "release (GitHub)") runs the fast tests and
       pushes the tag again, and the release workflow builds and publishes it.

    The only credential it uses is the one Git pushes with. -DryRun shows the plan and changes nothing.

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
    # Passed on to publish-release.ps1: tags without running the fast tests here first.
    [switch]$SkipTests,
    # Shows what would be done, and does nothing.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# --- Helpers: Invoke-Tool, Invoke-Git, Confirm-Step, Write-Step, Get-PublishedRelease and Get-UnfinishedReleaseRuns,
# shared with publish-release.ps1 ---

Import-Module (Join-Path $PSScriptRoot 'ReleaseCommon.psm1') -Force
Set-ReleaseContext -Root $root

# --- 1. The version and its tag ---

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]
Set-ReleaseContext -Repository $script:repo

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

# A published release is never redone: DNN Managers may have installed it, and nobody who has it would be offered
# the redone one. Not knowing counts as published.
try { $published = Get-PublishedRelease $tag }
catch { throw "GitHub couldn't be asked whether $tag is published ($($_.Exception.Message)) - nothing was changed. Try again later." }
if ($published) {
    Write-Host "GitHub has published $tag ($($published.html_url))." -ForegroundColor Red
    throw "A published release isn't redone - release the change as the next version (see .docs\releasing.md, A bad release). Nothing was changed."
}

# A release run on the tag that hasn't ended could still publish what it built while the tag is moved under it (its
# publish job checks the tag again, but its draft would be the old commit's). Not knowing counts as running.
try { $unfinished = @(Get-UnfinishedReleaseRuns $tag) }
catch { throw "GitHub couldn't be asked whether a release run on $tag is still going ($($_.Exception.Message)) - nothing was changed. Try again later." }
if ($unfinished.Count -gt 0) {
    $unfinished | ForEach-Object { Write-Host "  The release workflow on $tag is $($_.status): $($_.html_url)" -ForegroundColor Red }
    throw "Wait for it to end, or cancel it (a run waiting for approval: reject the job 'Publish the release'), then run this again. Nothing was changed."
}

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

# What is amended: the changes to files git has (git add -u) - new files only when you say so, one look at them first:
# a stray file (a log, a local setting, something with a secret in it) mustn't end up in a release by itself.
$tracked = @(Invoke-Git @('-c', 'core.quotepath=off', 'status', '--porcelain', '--untracked-files=no'))
$untracked = @(Invoke-Git @('-c', 'core.quotepath=off', 'ls-files', '--others', '--exclude-standard'))

if (-not $Message -and -not $DryRun) {
    Write-Host ''
    Write-Host "Release commit message (Enter keeps it):" -ForegroundColor Cyan
    Write-Host "  $releaseSubject"
    $typed = Read-Host 'New message'
    if ($typed.Trim()) { $Message = $typed.Trim() }
}
$newMessage = if ($Message) { $Message } else { $null }

$addNew = @()
if ($untracked.Count -gt 0) {
    Write-Host ''
    Write-Host "New files in your working copy, not in git yet:" -ForegroundColor Cyan
    $untracked | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
    if ($untracked.Count -gt 25) { Write-Host "  ... and $($untracked.Count - 25) more" }
    if ($DryRun) { Write-Host '  (asked when it isn''t a dry run - left out unless you say yes)' -ForegroundColor DarkGray }
    elseif (Confirm-Step "  Add these $($untracked.Count) new file(s) to the release commit too?") { $addNew = $untracked }
    else { Write-Host '  They stay out of the commit.' }
}
$changes = @($tracked) + @($addNew | ForEach-Object { "?? $_" })

if ($changes.Count -eq 0 -and -not $newMessage -and -not $localTagSha -and -not $remoteTagSha) {
    Write-Host 'Nothing to redo: no changes, no new message and no tag.' -ForegroundColor Yellow
    return
}

# --- 3. The plan ---

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
if ($remoteTagSha) { Write-Host "  Delete   the tag $tag on GitHub" }
if ($localTagSha) { Write-Host "  Delete   the tag $tag here" }
Write-Host "  Keep     a draft release $tag, if the failed run left one - the next run on the tag reuses it"
Write-Host '  Then     tag it again (publish-release.ps1) - asked at the end; GitHub builds and publishes it'

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run - nothing was changed.' -ForegroundColor Yellow
    return
}

Write-Host ''
if (-not (Confirm-Step "Redo $tag like this?")) { Write-Host 'Nothing was changed.' -ForegroundColor Yellow; return }

# --- 4. Do it: the commit and the branch first (nothing on GitHub is gone if they fail), then the tag ---

if ($changes.Count -gt 0 -or $newMessage) {
    Write-Step 'Commit'
    if ($tracked.Count -gt 0) { $null = Invoke-Git @('add', '--update') }
    if ($addNew.Count -gt 0) { $null = Invoke-Git (@('add', '--') + $addNew) }
    $amend = @('-C', $root, 'commit', '--amend')
    if ($newMessage) { $amend += @('-m', $newMessage) } else { $amend += '--no-edit' }
    Invoke-Tool git $amend
    $head = (Invoke-Git @('rev-parse', 'HEAD')) | Select-Object -First 1

    Write-Step "Push $branch"
    if ($pushedAlready) { Invoke-Tool git @('-C', $root, 'push', "--force-with-lease=$branch`:$remoteBranchSha", 'origin', $branch) }
    else { Invoke-Tool git @('-C', $root, 'push', 'origin', $branch) }
}

if ($remoteTagSha -or $localTagSha) {
    Write-Step "Delete the tag $tag"
    if ($remoteTagSha) { Invoke-Tool git @('-C', $root, 'push', 'origin', ":refs/tags/$tag") }
    if ($localTagSha) { Invoke-Tool git @('-C', $root, 'tag', '-d', $tag) }
}

# --- 5. Release it again ---

$short = $head.Substring(0, 7)
Write-Step "Release $tag from $short"
if (Confirm-Step "Release $tag again now (publish-release.ps1 - the fast tests here, then the tag; GitHub builds and publishes)?") {
    $publishArgs = @{ NotesFile = ".docs\release-notes\$tag.md"; Commit = $head }
    if ($SkipTests) { $publishArgs.SkipTests = $true }
    & (Join-Path $PSScriptRoot 'publish-release.ps1') @publishArgs
}
else {
    Write-Host ''
    Write-Host "Not released. $short is pushed without a tag - release it with the task ""release (GitHub)"", or run this again." -ForegroundColor Yellow
}
