<#
    What publish-release.ps1 and release-plan.ps1 share: a release's label and platforms (how they
    are written into the version tag and read back), running git and other tools, asking, and reading GitHub's
    public API without signing in (the repository is public) - no token is read or sent. Each script imports it and
    calls Set-ReleaseContext with the repository's folder, then with the GitHub repository (owner/name) once it is known.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Root = $null
$script:Repository = $null

function Set-ReleaseContext([string]$Root, [string]$Repository) {
    if ($Root) { $script:Root = $Root }
    if ($Repository) {
        if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "$Repository isn't a GitHub repository (owner/name)." }
        $script:Repository = $Repository
    }
}

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

# git in the repository's folder, its output returned.
function Invoke-Git([string[]]$argv) {
    if (-not $script:Root) { throw 'Set-ReleaseContext -Root first.' }
    Invoke-Tool git (@('-C', $script:Root) + $argv) -Quiet
}

function Confirm-Step([string]$question) {
    $answer = Read-Host "$question [y/N]"
    return $answer -match '^(y|yes)$'
}

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

# GitHub's public REST API (GET), without signing in. GitHub allows 60 such requests an hour from one address - enough
# to follow one release run.
function Invoke-GitHub([string]$url) {
    if ($url -notmatch '^https://') {
        if (-not $script:Repository) { throw 'Set-ReleaseContext -Repository first.' }
        $url = "https://api.github.com/repos/$script:Repository$url"
    }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'DnnManager-release' }
    return Invoke-RestMethod -Method GET -Uri $url -Headers $headers -UseBasicParsing
}

# The tag's published release, or $null. A draft isn't public - the release workflow reuses one, and a redo is for one.
function Get-PublishedRelease([string]$tag) {
    try { return Invoke-GitHub "/releases/tags/$tag" }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        if ($response -and $response.Value -and [int]$response.Value.StatusCode -eq 404) { return $null }
        throw
    }
}

# The release workflow's runs for the tag that haven't ended: queued, running, or waiting (for the publish job's
# approval, say). Throws when GitHub can't be asked.
function Get-UnfinishedReleaseRuns([string]$tag) {
    try { $answer = Invoke-GitHub "/actions/workflows/release.yml/runs?branch=$([uri]::EscapeDataString($tag))&event=push&per_page=20" }
    catch {
        # No release workflow on GitHub (yet): no run of it either.
        $response = $_.Exception.PSObject.Properties['Response']
        if ($response -and $response.Value -and [int]$response.Value.StatusCode -eq 404) { return @() }
        throw
    }
    @(@($answer.workflow_runs) | Where-Object { $_ -and $_.head_branch -eq $tag -and $_.status -ne 'completed' })
}

# --- What a release is: its label and its platforms, chosen when it is tagged ---
#
# The label is GitHub's: latest (what every DNN Manager's Update button offers), prerelease, or none (published, but
# not the latest). The platforms are what is built: windows (Setup and the portable exe), macos and linux (the
# cross-platform app's packages). publish-release.ps1 writes both into the version tag's message; the release
# workflow (release-plan.ps1) reads them back from it.

$script:ReleaseLabels = @('latest', 'prerelease', 'none')
$script:ReleasePlatforms = @('windows', 'macos', 'linux')

# The platforms in "windows,macos" / "windows macos" / an array - known ones only, each once, in the usual order.
function ConvertTo-ReleasePlatforms($Value) {
    $names = @(@($Value) | ForEach-Object { "$_" -split '[,\s]+' } | Where-Object { $_ } | ForEach-Object { $_.Trim().ToLowerInvariant() })
    $unknown = @($names | Where-Object { $_ -notin $script:ReleasePlatforms })
    if ($unknown) { throw "Unknown platform(s): $($unknown -join ', ') - choose from $($script:ReleasePlatforms -join ', ')." }
    @($script:ReleasePlatforms | Where-Object { $_ -in $names })
}

# Why this label and these platforms can't be released for the tag - or $null when they can.
function Get-ReleaseOptionsProblem([string]$Tag, [string]$Label, [string[]]$Platforms) {
    if ($Label -notin $script:ReleaseLabels) { return "'$Label' isn't a release label - choose latest, prerelease or none." }
    if (-not $Platforms -or $Platforms.Count -eq 0) { return 'No platform chosen - choose Windows, macOS, Linux or more.' }
    if ($Label -eq 'latest' -and $Tag -match '-') {
        return "$Tag is a pre-release's version: as the latest release, every DNN Manager's Update button would offer it. Choose Pre-release (or None)."
    }
    if ($Label -eq 'latest' -and 'windows' -notin $Platforms) {
        return 'The latest release is what every DNN Manager''s Update button downloads, so it needs the Windows files. Add Windows, or choose Pre-release or None.'
    }
    return $null
}

# The version tag's message: a title, and the release's options as two lines the workflow reads.
function ConvertTo-ReleaseTagMessage([string]$Tag, [string]$Label, [string[]]$Platforms) {
    "DNN Manager $Tag`n`nRelease-Label: $Label`nRelease-Platforms: $($Platforms -join ' ')`n"
}

# The options in a version tag's message. A tag without them (a lightweight tag, one made by hand): the label follows
# the version (v2.0.0-rc1 a pre-release, v2.0.0 the latest), and every platform is built.
function ConvertFrom-ReleaseTagMessage([string]$Tag, [string]$Message) {
    $label = if ($Tag -match '-') { 'prerelease' } else { 'latest' }
    $platforms = $script:ReleasePlatforms
    foreach ($line in "$Message" -split "`r?`n") {
        if ($line -match '^Release-Label:\s*(\S+)\s*$') { $label = $Matches[1].ToLowerInvariant() }
        elseif ($line -match '^Release-Platforms:\s*(.+?)\s*$') { $platforms = ConvertTo-ReleasePlatforms $Matches[1] }
    }
    [pscustomobject]@{ Label = $label; Platforms = @($platforms) }
}

# The label as GitHub writes it.
function Get-ReleaseLabelName([string]$Label) {
    switch ($Label) { 'latest' { 'Latest' } 'prerelease' { 'Pre-release' } default { 'None' } }
}

Export-ModuleMember -Function Set-ReleaseContext, Invoke-Tool, Invoke-Git, Confirm-Step, Write-Step, Invoke-GitHub, Get-PublishedRelease,
    Get-UnfinishedReleaseRuns, ConvertTo-ReleasePlatforms, Get-ReleaseOptionsProblem, ConvertTo-ReleaseTagMessage,
    ConvertFrom-ReleaseTagMessage, Get-ReleaseLabelName
