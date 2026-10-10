<#
.SYNOPSIS
    Puts the files the release workflow built on the tag's GitHub release (-Stage Draft), and publishes it once a person
    has approved (-Stage Publish). Run by .github\workflows\release.yml, not by hand.

.DESCRIPTION
    -Stage Draft (the "Draft the release" job):
    1. Checks every file against SHA256SUMS.txt - the files the signing job made, after the hand-over between the jobs.
    2. Finds the tag's release. A published one is never changed: a published version gets a new version number.
       A draft (a redo, or a run that stopped half way) is reused - its files are deleted and its notes replaced.
       Otherwise a draft is created, with the notes and the files' SHA-256 under them, for the commit the workflow
       built (target_commitish = GITHUB_SHA).
    3. Uploads the files to the draft and checks each one's size and GitHub's own SHA-256 of it (its digest - what
       every DNN Manager checks an update against) against the file here.

    -Stage Publish (the "Publish the release" job, in the environment "publish" - a required reviewer approves it):
    4. Checks again, right before publishing, that the tag still points at the commit the workflow built, and that
       every file on the draft is still the one in SHA256SUMS.txt (GitHub's digest of it).
    5. Publishes the release: as the latest one when its version is newer than the latest release's, or a pre-release
       for a vX.Y.Z-suffix tag (which nobody is offered). Anything that fails before this leaves the draft, to look into.

    It signs in with the job's GITHUB_TOKEN (GH_TOKEN), which may write this repository's contents and nothing else.
#>
[CmdletBinding()]
param(
    # The tag being released, vX.Y.Z or vX.Y.Z-suffix.
    [Parameter(Mandatory)] [string]$Tag,
    # The signing job's files: both exes, SHA256SUMS.txt and release-notes.md.
    [Parameter(Mandatory)] [string]$Directory,
    [Parameter(Mandatory)] [ValidateSet('Draft', 'Publish')] [string]$Stage,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    # The commit the workflow built: the tag must still point at it.
    [string]$Commit = $env:GITHUB_SHA
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "$Tag isn't a version tag like v1.9.0." }
$version = $Tag.Substring(1)
$prerelease = [bool]$Matches[2]
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "$Repository isn't a repository (owner/name)." }
if ($Commit -notmatch '^[0-9a-f]{40}$') { throw "'$Commit' isn't a commit (GITHUB_SHA)." }
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN isn''t set.' }

$assets = "DnnManager_Portable-$version-x64.exe", "DnnManager_Setup-$version-x64.exe"
$sumsPath = Join-Path $Directory 'SHA256SUMS.txt'
$notesPath = Join-Path $Directory 'release-notes.md'

function Invoke-GitHub([string]$method, [string]$url, $body, [string]$inFile) {
    if ($url -notmatch '^https://') { $url = "https://api.github.com/repos/$Repository$url" }
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $params = @{ Method = $method; Uri = $url; Headers = $headers; ContentType = 'application/json; charset=utf-8' }
    if ($null -ne $body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 5)) }
    if ($inFile) { $params.InFile = $inFile; $params.ContentType = 'application/octet-stream'; $params.TimeoutSec = 1800 }
    return Invoke-RestMethod @params
}

# GitHub's answer, or $null when it says 404.
function Get-GitHubOrNull([string]$url) {
    try { return Invoke-GitHub GET $url }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        if ($response -and $response.Value -and [int]$response.Value.StatusCode -eq 404) { return $null }
        throw
    }
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# The asset's digest as GitHub lists it ('' when it lists none yet).
function Get-Digest($asset) {
    $property = $asset.PSObject.Properties['digest']
    if ($property -and $property.Value) { return "$($property.Value)" }
    return ''
}

# The tag's release on GitHub - a draft too, which isn't found by tag - or $null. More than one is an error.
function Find-Release {
    $all = Invoke-GitHub GET '/releases?per_page=100'
    $found = @(foreach ($r in $all) { if ([string]$r.tag_name -eq $Tag) { $r } })
    if ($found.Count -gt 1) { throw "GitHub has $($found.Count) releases with the tag $Tag - delete the extra drafts by hand." }
    if ($found.Count -eq 1) { return $found[0] }
    return $null
}

# The commit the tag points at now - through an annotated tag's object to its commit.
function Get-TagCommit {
    $ref = Get-GitHubOrNull "/git/ref/tags/$Tag"
    if (-not $ref) { return $null }
    $object = $ref.object
    for ($i = 0; $i -lt 5 -and $object.type -eq 'tag'; $i++) { $object = (Invoke-GitHub GET "/git/tags/$($object.sha)").object }
    if ($object.type -ne 'commit') { return $null }
    return [string]$object.sha
}

# A version as numbers and a pre-release suffix, comparable: 1.10.0 > 1.9.3, 1.9.0 > 1.9.0-rc.1.
function Test-Newer([string]$a, [string]$b) {
    $pa = $a -split '-', 2; $pb = $b -split '-', 2
    $va = [version]$pa[0]; $vb = [version]$pb[0]
    if ($va -ne $vb) { return $va -gt $vb }
    # The same numbers: the release is newer than its pre-releases.
    return ($pa.Count -eq 1) -and ($pb.Count -eq 2)
}

# --- 1. The files, as the signing job made them ---

$expected = @{}
foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
    if ($line -match '^([0-9a-f]{64})  (\S+)$') { $expected[$Matches[2]] = $Matches[1] }
}
foreach ($name in $assets) {
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path)) { throw "$name is missing." }
    if (-not $expected.ContainsKey($name)) { throw "SHA256SUMS.txt doesn't list $name." }
    if ((Get-Sha256 $path) -ne $expected[$name]) { throw "$name isn't the file the signing job made (its SHA-256 differs from SHA256SUMS.txt)." }
}
Write-Host "Files checked against SHA256SUMS.txt: $($assets -join ', ')"
$sumsText = [IO.File]::ReadAllText($sumsPath).Trim()

if ($Stage -eq 'Draft') {
    # --- 2. The tag's release: never a published one ---

    $body = [IO.File]::ReadAllText($notesPath).TrimEnd() + "`n`n**SHA-256**`n`n``````text`n$sumsText`n```````n"
    $fields = @{ tag_name = $Tag; target_commitish = $Commit; name = $Tag; body = $body; draft = $true; prerelease = $prerelease }

    $release = Find-Release
    if ($release -and -not $release.draft) {
        throw "The release $Tag is already published ($($release.html_url)) - it isn't changed. Release the change as the next version."
    }
    if ($release) {
        Write-Host "Reusing the draft $Tag (id $($release.id)): its files are replaced."
        foreach ($old in @(Invoke-GitHub GET "/releases/$($release.id)/assets?per_page=100")) {
            if ($old) { $null = Invoke-GitHub DELETE "/releases/assets/$($old.id)" }
        }
        $release = Invoke-GitHub PATCH "/releases/$($release.id)" $fields
    }
    else {
        $release = Invoke-GitHub POST '/releases' $fields
        Write-Host "Draft $Tag created (id $($release.id)) for $Commit."
    }

    # --- 3. Upload, and check GitHub's SHA-256 of each file ---

    foreach ($name in $assets + 'SHA256SUMS.txt') {
        $path = Join-Path $Directory $name
        $size = (Get-Item -LiteralPath $path).Length
        Write-Host "Uploading $name ($([math]::Round($size / 1MB, 1)) MB)..."
        $asset = Invoke-GitHub POST "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($name))" $null $path
        if ($asset.size -ne $size) { throw "$name arrived with $($asset.size) bytes, expected $size. The draft is left to look into." }
        $local = Get-Sha256 $path
        $digest = Get-Digest $asset
        for ($try = 1; $try -lt 5 -and -not $digest; $try++) {
            Start-Sleep -Seconds 2
            $digest = Get-Digest (Invoke-GitHub GET "/releases/assets/$($asset.id)")
        }
        if ($digest -ne "sha256:$local") {
            throw "$name : GitHub lists $(if ($digest) { $digest } else { 'no SHA-256' }), not sha256:$local. The draft is left to look into."
        }
        Write-Host "  GitHub's SHA-256 matches: $local"
    }
    Write-Host "The draft $Tag is ready: $($release.html_url) - it is published once the publish job is approved."
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = "### Draft [$Tag]($($release.html_url)) is ready$(if ($prerelease) { ' (pre-release)' })`n`nApprove the job **Publish the release** to publish it.`n`n``````text`n$sumsText`n```````n"
        [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $summary, (New-Object Text.UTF8Encoding $false))
    }
    return
}

# --- 4. Right before publishing: the tag, the draft and its files are still what was built and checked ---

$tagCommit = Get-TagCommit
if ($tagCommit -ne $Commit) {
    throw "The tag $Tag points at $(if ($tagCommit) { $tagCommit } else { 'nothing' }) now, not at $Commit, which this run built - not published. The draft is left to look into."
}
$release = Find-Release
if (-not $release) { throw "There is no draft release $Tag to publish." }
if (-not $release.draft) { throw "The release $Tag is already published ($($release.html_url)) - it isn't changed." }
$onDraft = @(Invoke-GitHub GET "/releases/$($release.id)/assets?per_page=100")
$names = @($onDraft | ForEach-Object { [string]$_.name })
foreach ($extra in $names | Where-Object { $_ -notin ($assets + 'SHA256SUMS.txt') }) { throw "The draft has a file this run didn't upload: $extra - not published." }
foreach ($name in $assets) {
    $asset = $onDraft | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { throw "The draft has no $name - not published." }
    if ((Get-Digest $asset) -ne "sha256:$($expected[$name])") { throw "$name on the draft isn't the file this run built (GitHub lists $(Get-Digest $asset)) - not published." }
}
Write-Host "The tag points at $Commit, and the draft's files are the ones built."

# --- 5. Publish ---

# The latest release only when this version is newer than the latest one now - a fix to an older line (or a redo
# released after a newer version) mustn't become what every DNN Manager is offered.
$makeLatest = 'false'
if (-not $prerelease) {
    $latest = Get-GitHubOrNull '/releases/latest'
    $latestVersion = if ($latest) { ([string]$latest.tag_name).TrimStart('v') } else { $null }
    if (-not $latestVersion -or $latestVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' -or (Test-Newer $version $latestVersion)) { $makeLatest = 'true' }
    else { Write-Host "The latest release is $latestVersion, newer than $version - $Tag is published without becoming the latest." -ForegroundColor Yellow }
}
$published = Invoke-GitHub PATCH "/releases/$($release.id)" @{ draft = $false; make_latest = $makeLatest }
Write-Host "Published $($published.html_url)$(if ($makeLatest -eq 'true') { ' as the latest release' })"
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = "### Published [$Tag]($($published.html_url))$(if ($prerelease) { ' (pre-release)' } elseif ($makeLatest -ne 'true') { ' (not the latest)' })`n`n``````text`n$sumsText`n```````n"
    [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $summary, (New-Object Text.UTF8Encoding $false))
}
