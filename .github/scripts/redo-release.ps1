<#
.SYNOPSIS
    Redoes a release whose tag is made but which isn't published: releases that version again from the newest commit
    (or the one you pick) - the old tag is deleted and made again there.

.DESCRIPTION
    For a release whose workflow failed or that needs more changes before it is published: commit and push them, then
    run this. A published release is never redone - not even a pre-release: running DNN Managers may have installed
    it, and with immutable releases its tag can't move. Release the change as the next version instead.
    1. The version: -Version, or the newest version tag.
    2. Refuses when GitHub has published that release, or while a release workflow run on its tag hasn't ended
       (queued, running, or waiting for the publish job's approval) - asked without signing in; refused too when
       GitHub can't be asked.
    3. The commit to release from: -Commit, or picked from the commits of the current branch since the tag's (newest
       first - Enter takes the newest). It is released as it is: nothing is amended or pushed, and your working copy
       isn't used.
    4. Shows the plan and asks once, then deletes the tag here and on GitHub and releases the version from that commit
       (publish-release.ps1: the fast tests here, then the tag). A draft release the failed run left is kept: the next
       release workflow run on the tag reuses it, replacing its files and notes.

    The only credential it uses is the one Git pushes with. -DryRun shows the plan and changes nothing.

.EXAMPLE
    .github\scripts\redo-release.ps1
.EXAMPLE
    .github\scripts\redo-release.ps1 -Version 1.7.6 -Commit 9030c5f
.EXAMPLE
    .github\scripts\redo-release.ps1 -DryRun
#>
[CmdletBinding()]
param(
    # The version to redo (1.7.6 or v1.7.6); the newest version tag when omitted.
    [string]$Version,
    # The commit to release from (a hash); picked here when omitted - Enter takes the newest.
    [string]$Commit,
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

$localTag = (Invoke-Git @('tag', '--list', $tag)) | Select-Object -First 1
$localTagSha = if ($localTag) { (Invoke-Git @('rev-parse', "refs/tags/$tag^{commit}")) | Select-Object -First 1 } else { $null }
# An annotated tag is listed twice - the tag, then (^{}) the commit it points at.
$remoteTagLines = @(Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag"))
$remoteTagLine = @($remoteTagLines | Where-Object { $_ -like '*^{}' }) + @($remoteTagLines) | Select-Object -First 1
$remoteTagSha = if ($remoteTagLine) { ($remoteTagLine -split "`t")[0] } else { $null }
if ($localTagSha -and $remoteTagSha -and $localTagSha -ne $remoteTagSha) {
    throw "Tag $tag points at $($localTagSha.Substring(0, 7)) here but at $($remoteTagSha.Substring(0, 7)) on GitHub - sort that out first."
}
$tagSha = if ($localTagSha) { $localTagSha } elseif ($remoteTagSha) { $remoteTagSha } else { $null }

# --- 2. The commit to release from: the newest of the branch, or one picked since the tag's ---

# The commits after the tag's, newest first, then the tag's own - without a tag, the newest few.
$choices = if ($tagSha) {
    & git -C $root merge-base --is-ancestor $tagSha $head
    if ($LASTEXITCODE -ne 0) { throw "Tag $tag points at $($tagSha.Substring(0, 7)), which isn't on $branch." }
    @(Invoke-Git @('log', '--format=%H %s', '--ancestry-path', "$tagSha..HEAD")) + @(Invoke-Git @('log', '-1', '--format=%H %s', $tagSha))
} else {
    @(Invoke-Git @('log', '--format=%H %s', '-10', 'HEAD'))
}
$choices = @($choices | Where-Object { $_ } | Select-Object -Unique)

if ($Commit) {
    $chosenSha = (Invoke-Git @('rev-parse', '--verify', "$Commit^{commit}")) | Select-Object -First 1
    & git -C $root merge-base --is-ancestor $chosenSha $head
    if ($LASTEXITCODE -ne 0) { throw "$Commit isn't on $branch." }
}
elseif ($choices.Count -eq 1 -or $DryRun) {
    $chosenSha = $choices[0].Substring(0, 40)
}
else {
    Write-Host ''
    Write-Host "Release $tag from:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $choices.Count; $i++) {
        $sha = $choices[$i].Substring(0, 40)
        $label = if ($sha -eq $tagSha) { ' (the tag''s commit now)' } else { '' }
        Write-Host ("  {0}. {1} {2}{3}" -f ($i + 1), $sha.Substring(0, 7), $choices[$i].Substring(41), $label)
    }
    $picked = Read-Host 'Number (Enter = 1, the newest)'
    $index = if ($picked.Trim()) { [int]$picked.Trim() - 1 } else { 0 }
    if ($index -lt 0 -or $index -ge $choices.Count) { throw "There is no choice $picked - nothing was changed." }
    $chosenSha = $choices[$index].Substring(0, 40)
}
$chosenShort = $chosenSha.Substring(0, 7)
$chosenSubject = (Invoke-Git @('log', '-1', '--format=%s', $chosenSha)) | Select-Object -First 1
$onGitHub = @(Invoke-Git @('branch', '-r', '--contains', $chosenSha)).Count -gt 0

# --- 3. The plan ---

Write-Step "Redo $tag"
Write-Host "  Release  $chosenShort ""$chosenSubject"" as it is - your working copy isn't used"
if (-not $onGitHub) { Write-Host "  Note     $chosenShort isn't on GitHub yet - push $branch first (the release asks before it tags one that isn't)" -ForegroundColor Yellow }
$uncommitted = @(Invoke-Git @('status', '--porcelain', '--untracked-files=no'))
if ($uncommitted.Count -gt 0) { Write-Host "  Note     $($uncommitted.Count) changed file(s) aren't committed - they aren't in the release" -ForegroundColor Yellow }
if ($remoteTagSha) { Write-Host "  Delete   the tag $tag on GitHub" }
if ($localTagSha) { Write-Host "  Delete   the tag $tag here" }
Write-Host "  Keep     a draft release $tag, if the failed run left one - the next run on the tag reuses it"
Write-Host "  Then     tag $chosenShort (publish-release.ps1: the fast tests here, then the tag) - GitHub builds and publishes it"

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run - nothing was changed.' -ForegroundColor Yellow
    return
}

Write-Host ''
if (-not (Confirm-Step "Redo $tag from $chosenShort like this?")) { Write-Host 'Nothing was changed.' -ForegroundColor Yellow; return }

# --- 4. Do it: the tag goes, then the release task makes it again on the chosen commit ---

if ($remoteTagSha -or $localTagSha) {
    Write-Step "Delete the tag $tag"
    if ($remoteTagSha) { Invoke-Tool git @('-C', $root, 'push', 'origin', ":refs/tags/$tag") }
    if ($localTagSha) { Invoke-Tool git @('-C', $root, 'tag', '-d', $tag) }
}

Write-Step "Release $tag from $chosenShort"
$publishArgs = @{ NotesFile = ".docs\release-notes\$tag.md"; Commit = $chosenSha }
if ($SkipTests) { $publishArgs.SkipTests = $true }
& (Join-Path $PSScriptRoot 'publish-release.ps1') @publishArgs
