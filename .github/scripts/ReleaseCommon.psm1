<#
    What publish-release.ps1 and redo-release.ps1 share: running git and other tools, asking, and reading GitHub's
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

Export-ModuleMember -Function Set-ReleaseContext, Invoke-Tool, Invoke-Git, Confirm-Step, Write-Step, Invoke-GitHub, Get-PublishedRelease, Get-UnfinishedReleaseRuns
