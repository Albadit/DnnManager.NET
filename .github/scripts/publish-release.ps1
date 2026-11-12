<#
.SYNOPSIS
    Starts a DNN Manager release from this PC: pick a release-notes file and a commit on GitHub, and it releases
    that commit once CI has passed on it - it pushes the version tag, and GitHub builds, attests and publishes the release.

.DESCRIPTION
    1. Lists .docs\release-notes\vX.Y.Z.md - the file's name is the version, the tag and the release title.
    2. The commit: picked from the 20 newest of the current branch on GitHub (the VS Code task's picker, -List
       commits; the newest on top), or -Commit names it - a hash, or "latest" for the newest.
       Then the release label, as GitHub names it - Latest (what every DNN Manager's Update button offers),
       Pre-release or None - and the platforms to build: Windows (Setup and the portable exe), macOS and Linux (the
       cross-platform app's packages). Both go into the version tag's message, which the release workflow reads.
    3. Checks the tag is free and GitHub has no published release of it, that the commit has the release notes
       and the release workflow (.github\workflows\release.yml) - the release is built from that commit, notes
       included - and that the label fits: a vX.Y.Z-suffix version or a release without Windows is never Latest.
    4. Tags the commit and pushes the tag at once - nothing runs or waits here. On GitHub, the release workflow's
       first job waits for CI (.github\workflows\ci.yml, which tests every commit pushed) on that commit and builds
       only once it has passed; a failed CI stops the release there, before anything is built. Nothing is tested twice.
    5. Nothing built on this PC is uploaded: the release
       workflow builds the portable exe and the installer from the tag, signs the files
       when signing is set up, attests their provenance, tries the installed and the portable exe, puts them on a
       draft release, and publishes it only once all of that has passed and a reviewer has approved the publish job
       (a failure leaves at most a draft). The script stops once the tag is pushed and names the run's page.

    The only credential it uses is the one Git pushes the tag with. GitHub's public API is asked - without signing
    in - whether the release is published already. The VS Code task
    "Release" asks for the notes file, the commit, the label and the platforms in VS Code's pickers (filled by
    -List); run directly, it asks in the terminal.

.EXAMPLE
    .github\scripts\publish-release.ps1
.EXAMPLE
    .github\scripts\publish-release.ps1 -NotesFile .docs\release-notes\v1.7.0.md -Commit latest
.EXAMPLE
    .github\scripts\publish-release.ps1 -NotesFile .docs\release-notes\v2.0.0-rc1.md -Label prerelease -Platforms windows,macos,linux
.EXAMPLE
    .github\scripts\publish-release.ps1 -List commits
#>
[CmdletBinding()]
param(
    # The release-notes file to release; asked for when omitted.
    [string]$NotesFile,
    # The commit to release: a hash, or "latest" - the newest of the current branch on GitHub; asked for when omitted.
    [string]$Commit,
    # GitHub's release label: latest, prerelease or none; asked for when omitted.
    [ValidateSet('', 'latest', 'prerelease', 'none')]
    [string]$Label,
    # What is built: windows, macos, linux - one or more ("windows,macos"); asked for when omitted.
    [string[]]$Platforms,
    # Only prints the choices for VS Code's picker (Tasks Shell Input), one "value||label||description||detail" per line.
    [ValidateSet('notes', 'commits', 'platforms')]
    [string]$List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$notesDir = Join-Path $root '.docs\release-notes'

# --- Helpers (Invoke-Tool, Invoke-Git, Confirm-Step, Write-Step, Invoke-GitHub and Get-PublishedRelease are in
# ReleaseCommon.psm1, shared with release-plan.ps1) ---

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

# The platforms for VS Code's picker (several can be ticked) - asks nothing of git or GitHub.
if ($List -eq 'platforms') {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    'windows||$(device-desktop) Windows||Setup and the portable exe||The Windows app, with IIS - what the Update button downloads'
    'macos||$(device-desktop) macOS||Apple Silicon and Intel||DNN Manager.app in a zip, signed ad hoc (free), not notarized'
    'linux||$(terminal-linux) Linux||x64 and arm64||The program in a tar.gz, with a .desktop file'
    return
}

# --- The choices: release-notes files and the commits on GitHub ---

$origin = (Invoke-Git @('remote', 'get-url', 'origin')) | Select-Object -First 1
if ($origin -notmatch 'github\.com[:/](.+?)(\.git)?$') { throw "origin ($origin) isn't a GitHub repository." }
$script:repo = $Matches[1]
Set-ReleaseContext -Repository $script:repo

# The notes files - the not yet tagged ones first (the next release on top), then the released ones; newest first.
# The version tags GitHub has (git ls-remote) - not the ones only this PC has.
function Get-RemoteTags {
    @(Invoke-Git @('ls-remote', '--tags', 'origin', 'refs/tags/v*') | ForEach-Object { ($_ -split "`t")[1] -replace '^refs/tags/', '' -replace '\^\{\}$', '' } |
            Where-Object { $_ } | Select-Object -Unique)
}

# The version after a published one, to release a change as: 2.0.0-rc1 -> 2.0.0-rc2, 1.8.7 -> 1.8.8.
function Get-NextVersion([string]$tag) {
    $v = $tag.TrimStart('v')
    if ($v -match '-' -and $v -match '^(.*?)(\d+)$') { return "v$($Matches[1])$([int]$Matches[2] + 1)" }
    if ($v -match '^(\d+)\.(\d+)\.(\d+)') { return "v$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)" }
    return 'the next version'
}

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
        # Each version as GitHub has it: published (live), tagged but not published (released again when picked), or new.
        $remoteTags = @(Get-RemoteTags)
        $published = @()
        # Windows PowerShell's Invoke-RestMethod hands a JSON list back as ONE object: foreach goes through its items.
        $prereleases = @()
        try {
            $all = Invoke-GitHub '/releases?per_page=100'
            $published = @(foreach ($r in $all) { if ($r -and -not $r.draft) { [string]$r.tag_name } })
            $prereleases = @(foreach ($r in $all) { if ($r -and -not $r.draft -and $r.prerelease) { [string]$r.tag_name } })
        }
        catch { }
        $next = $true
        foreach ($f in Get-NotesFiles $remoteTags) {
            $name = $f.BaseName
            if ($prereleases -contains $name) { $icon = '$(sync)'; $state = 'published pre-release - replaced by the commit you pick' }
            elseif ($published -contains $name) { $icon = '$(lock)'; $state = "published - live; release the change as $(Get-NextVersion $name)" }
            elseif ($remoteTags -contains $name) { $icon = '$(sync)'; $state = 'tagged, not published - released again from the commit you pick' }
            else { $icon = '$(rocket)'; $state = if ($next) { 'next release' } else { 'not released' }; $next = $false }
            ".docs/release-notes/$($f.Name)||$icon $name||$state||$(& $clean (Get-NotesSummary $f))"
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
$remoteTags = @(Get-RemoteTags)

# --- 1. The release-notes file: its name is the version, the tag and the title ---

$files = @(Get-NotesFiles $remoteTags)
if ($files.Count -eq 0) { throw "No vX.Y.Z.md files in $notesDir - write the release notes first." }
if ($NotesFile) {
    $path = if ([IO.Path]::IsPathRooted($NotesFile)) { $NotesFile } else { Join-Path $root $NotesFile }
    $notes = $files | Where-Object { $_.FullName -eq [IO.Path]::GetFullPath($path) } | Select-Object -First 1
    if (-not $notes) { throw "$NotesFile isn't a release-notes file (.docs\release-notes\vX.Y.Z.md)." }
}
else {
    $labels = $files | ForEach-Object { if ($remoteTags -contains $_.BaseName) { "$($_.Name)  (tagged on GitHub - published, or released again)" } else { $_.Name } }
    $notes = $files[(Read-Choice 'Release notes (the file name is the tag and the title):' $labels 0)]
}

$tag = $notes.BaseName
$version = $tag.Substring(1)
$null = $version -match '^(\d+\.\d+\.\d+)(-.+)?$'
$prerelease = [bool]$Matches[2]

# --- 2. The commit ---

if ($Commit -eq 'latest') {
    # The newest commit of the branch as GitHub has it (fetched above) - what CI tested last.
    $commits = @(Get-GitHubCommits)
    if ($commits.Count -eq 0) { throw 'No commits found on origin.' }
    $selected = $commits[0]
    Write-Host "Commit: the newest on GitHub ($($selected.Ref)) - $($selected.Short) $($selected.Subject)"
}
elseif ($Commit) {
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

# --- The label and the platforms ---

if (-not $Label) {
    # A vX.Y.Z-suffix version is offered as a pre-release first; any other as the latest.
    $Label = @('latest', 'prerelease', 'none')[(Read-Choice 'Release label:' @(
                'Latest       - the latest release: every DNN Manager''s Update button offers it',
                'Pre-release  - not ready for everyone: only those who download it get it',
                'None         - published, but not the latest') $(if ($prerelease) { 1 } else { 0 }))]
}
if ($Platforms) { $Platforms = ConvertTo-ReleasePlatforms $Platforms }
else {
    Write-Host ''
    Write-Host 'Platforms to build: windows (Setup, portable exe), macos, linux' -ForegroundColor Cyan
    $answer = Read-Host 'Which, separated by commas (Enter = all three)'
    $Platforms = if ([string]::IsNullOrWhiteSpace($answer)) { ConvertTo-ReleasePlatforms 'windows macos linux' } else { ConvertTo-ReleasePlatforms $answer }
}

# --- 3. Checks before tagging ---

Write-Step 'Checks'
$problems = @()
if ($optionsProblem = Get-ReleaseOptionsProblem $tag $Label $Platforms) { $problems += $optionsProblem }
$remoteTag = (Invoke-Git @('ls-remote', '--tags', 'origin', "refs/tags/$tag")) | Select-Object -First 1
$remoteTagSha = if ($remoteTag) { ($remoteTag -split "`t")[0] } else { $null }
$published = $null
$publishedUnknown = $false
try { $published = Get-PublishedRelease $tag }
catch {
    $publishedUnknown = $true
    Write-Host "  GitHub couldn't be asked whether $tag is published ($($_.Exception.Message))." -ForegroundColor Yellow
}
# A version on GitHub already: released again when it isn't published (a failed run, a draft not approved) - never
# when it is. A published version stays as it is: every DNN Manager updates only to a newer version, so whoever has it
# would never be offered the new build - the change goes out as the next version.
$redo = $false
$replace = $false
if ($published -and -not $published.prerelease) {
    # Latest (or None): every DNN Manager may have it, and updates only to a newer version.
    $problems += "$tag is published (live): $($published.html_url). A published release that isn't a pre-release is never released again - whoever installed it wouldn't be offered the new build. Write the notes as $(Get-NextVersion $tag).md and release that."
}
elseif ($published) {
    # A published pre-release, which no Update button offers: replaced - the release workflow deletes it on GitHub once
    # CI has passed on the new commit, and drafts the new build in its place.
    $redo = $true
    $replace = $true
    try {
        foreach ($run in @(Get-UnfinishedReleaseRuns $tag)) {
            $problems += "The release run on $tag hasn't ended ($($run.status -replace '_', ' '), $($run.html_url)) - wait for it, or cancel it, then release again."
        }
    }
    catch { $problems += "Whether a release run on $tag is still going couldn't be checked ($($_.Exception.Message)) - nothing is changed. Try again." }
}
elseif ($remoteTag) {
    $redo = $true
    if ($publishedUnknown) { $problems += "$tag is on GitHub already, and whether it is published couldn't be checked - a published one is never released again, so nothing is changed. Try again." }
    else {
        try {
            foreach ($run in @(Get-UnfinishedReleaseRuns $tag)) {
                $problems += "The release run on $tag hasn't ended ($($run.status -replace '_', ' '), $($run.html_url)) - wait for it, or cancel it (reject 'Publish the release'), then release again."
            }
        }
        catch { $problems += "Whether a release run on $tag is still going couldn't be checked ($($_.Exception.Message)) - nothing is changed. Try again." }
    }
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
if ($replace) {
    Write-Host "  $tag is a published pre-release ($($published.html_url)): it is REPLACED by $($selected.Short) - the tag moves now; once CI has passed," -ForegroundColor Yellow
    Write-Host "  the release workflow deletes the published $tag on GitHub and drafts the new build in its place." -ForegroundColor Yellow
}
elseif ($redo) {
    $from = if ($remoteTagSha) { $remoteTagSha.Substring(0, 7) } else { '?' }
    Write-Host "  $tag is tagged on GitHub ($from) but not published: it is released again from $($selected.Short) - the tag moves, and the draft the earlier run left is reused." -ForegroundColor Yellow
}
else { Write-Host "  Tag $tag is free, and $($selected.Short) has its release notes and the release workflow." }

$onRemote = @(Invoke-Git @('branch', '-r', '--contains', $selected.Sha))
if ($onRemote.Count -eq 0) {
    # CI tests what is pushed to a branch, and the release workflow builds only once CI has passed on the commit: a
    # commit that is only here would never get there.
    Write-Host "  $($selected.Short) isn't on GitHub yet - push $branch first: CI tests it there, and the release waits for that." -ForegroundColor Red
    throw 'Nothing was tagged or published.'
}

# The SDK the release is built with is the one global.json names in that commit. Once a newer one is out - a .NET
# security patch - the older SDK's Native AOT compiler is a known vulnerability, and the restore below fails (NU1903).
try {
    $sdk = ((Invoke-Git @('show', "$($selected.Sha):global.json")) -join "`n" | ConvertFrom-Json).sdk.version
    if ($sdk -match '^(\d+\.\d+)\.\d+$') {
        $sdkReleases = Invoke-RestMethod -UseBasicParsing "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$($Matches[1])/releases.json"
        $newestSdk = [string]$sdkReleases.'latest-sdk'
        if ($newestSdk -match '^\d+\.\d+\.\d+$' -and [version]$newestSdk -gt [version]$sdk) {
            Write-Host "  $($selected.Short) builds with .NET SDK $sdk, but $newestSdk is out (runtime $($sdkReleases.'latest-runtime')," -ForegroundColor Yellow
            Write-Host "  $($sdkReleases.'latest-release-date'), security fix: $($sdkReleases.releases[0].security)). Run the task ""Update .NET SDK"", commit and push," -ForegroundColor Yellow
            Write-Host '  then release that commit - an SDK with a known vulnerability fails the restore below (NU1903).' -ForegroundColor Yellow
        }
        else { Write-Host "  .NET SDK $sdk is the newest of its channel." }
    }
}
catch { Write-Host "  Whether a newer .NET SDK is out couldn't be checked ($($_.Exception.Message))." -ForegroundColor Yellow }

Write-Host ''
$platformNames = ($Platforms | ForEach-Object { @{ windows = 'Windows'; macos = 'macOS'; linux = 'Linux' }[$_] }) -join ', '
Write-Host "  Version  $version"
Write-Host "  Label    $(Get-ReleaseLabelName $Label)"
Write-Host "  Builds   $platformNames"
Write-Host "  Notes    .docs\release-notes\$($notes.Name)"
Write-Host "  Commit   $($selected.Short) $($selected.Subject)"
Write-Host "           $($selected.Author), $($selected.Date)"

# --- 4. What is released ---

Write-Step 'Ready to release'
$what = @()
if ('windows' -in $Platforms) { $what += 'both Windows exes' }
$desktopNames = @($Platforms | Where-Object { $_ -ne 'windows' } | ForEach-Object { @{ macos = 'macOS'; linux = 'Linux' }[$_] })
if ($desktopNames) { $what += "the $($desktopNames -join ' and ') packages" }
$as = switch ($Label) { 'latest' { 'as the latest release' } 'prerelease' { 'as a pre-release' } default { 'as a release that isn''t the latest (label None)' } }
Write-Host "  Tag      $tag -> $($selected.Short), pushed to origin, with the label and the platforms in its message"
Write-Host "  Then     GitHub's release workflow builds $($what -join ' and ') from it, attests them and publishes"
Write-Host "           $tag $as with .docs\release-notes\$($notes.Name)"
Write-Host "  First    it waits on GitHub for CI on $($selected.Short) - it builds only once CI has passed, nothing is tested twice"

# --- 5. The tag: pushed at once - GitHub waits for CI, then builds and publishes the release ---

Write-Step "Tag $tag"
# Released again: the tag on GitHub (not published - checked above) goes first; the run on the new one reuses its draft.
if ($redo) { $null = Invoke-Tool git @('-C', $root, 'push', 'origin', ":refs/tags/$tag") }
# An annotated tag: its message carries the label and the platforms the release workflow builds (release-plan.ps1).
# A tag only here (made by hand, or left from an old attempt) is replaced - it isn't on GitHub.
$message = Join-Path ([IO.Path]::GetTempPath()) "dnnmanager-tag-$([guid]::NewGuid().ToString('N')).txt"
try {
    [IO.File]::WriteAllText($message, (ConvertTo-ReleaseTagMessage $tag $Label $Platforms -Replace:$replace), [Text.UTF8Encoding]::new($false))
    $null = Invoke-Git @('tag', '--force', '--annotate', '--file', $message, $tag, $selected.Sha)
}
finally { Remove-Item $message -ErrorAction SilentlyContinue }
$null = Invoke-Tool git @('-C', $root, 'push', 'origin', "refs/tags/$tag")

# Not followed from here: the run is on GitHub - and asking its API every few minutes would use up the hourly
# allowance the release scripts share (without signing in).
Write-Host ''
Write-Host "Tag pushed - you're done here. On GitHub the release waits for CI on $($selected.Short), then builds $($tag):" -ForegroundColor Green
Write-Host "  https://github.com/$script:repo/actions/workflows/release.yml"
Write-Host "Once the draft is ready, approve the job 'Publish the release' there to publish it."
