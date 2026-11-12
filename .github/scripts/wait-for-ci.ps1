<#
.SYNOPSIS
    Waits for CI (.github\workflows\ci.yml) on a commit and succeeds only once it has passed there. Run by the "Plan"
    job of .github\workflows\release.yml: a release is built only from a commit CI has tested - and nothing is tested
    twice: the VS Code task "Release" pushes the tag at once, and this waits on GitHub instead of on your PC.

.DESCRIPTION
    Asks GitHub every 30 seconds for the newest CI run on the commit (with the job's GITHUB_TOKEN, GH_TOKEN):
      passed                  done - the release is built.
      failed or cancelled     fails, naming the jobs that didn't pass and the step each stopped at - nothing is built.
      running, queued         waits, up to -TimeoutMinutes.
      no run                  waits -NoRunMinutes for one to start (CI starts when the commit is pushed to a branch),
                              then fails: a commit that was only tagged, never pushed to a branch, isn't tested.
    A cancelled CI is usually a newer push to the same branch (CI cancels the older run): release that commit.

.EXAMPLE
    .github\scripts\wait-for-ci.ps1 -Sha 092be94a1b86d49e84b65b3dd860a2a02219cc90 -Repository Albadit/DnnManager.NET
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Sha,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [int]$TimeoutMinutes = 90,
    [int]$NoRunMinutes = 10,
    [int]$IntervalSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Sha -notmatch '^[0-9a-f]{40}$') { throw "'$Sha' isn't a commit (40 hex characters)." }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "$Repository isn't a repository (owner/name)." }
$headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'DnnManager-release' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$api = "https://api.github.com/repos/$Repository"
$short = $Sha.Substring(0, 7)

function Get-CiRun {
    $answer = Invoke-RestMethod -Uri "$api/actions/workflows/ci.yml/runs?head_sha=$Sha&per_page=10" -Headers $headers
    @(@($answer.workflow_runs) | Where-Object { $_ -and $_.head_sha -eq $Sha } | Sort-Object { [datetime]$_.created_at } -Descending) | Select-Object -First 1
}

$started = Get-Date
$said = ''
while ($true) {
    $run = $null
    try { $run = Get-CiRun }
    catch { Write-Host "GitHub couldn't be asked ($($_.Exception.Message)) - asking again." }
    $state = if (-not $run) { 'not started' } elseif ($run.status -ne 'completed') { $run.status } else { $run.conclusion }
    if ($state -ne $said) {
        Write-Host ("{0:HH:mm:ss}  CI on {1}: {2}{3}" -f (Get-Date), $short, $state, $(if ($run) { " - $($run.html_url)" } else { '' }))
        $said = $state
    }

    if ($run -and $run.status -eq 'completed') {
        if ($run.conclusion -eq 'success') {
            Write-Host "CI passed on $short - the release is built from it."
            if ($env:GITHUB_STEP_SUMMARY) { [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, "CI passed on ``$short``: [$($run.html_url)]($($run.html_url))`n") }
            return
        }
        $jobs = @((Invoke-RestMethod -Uri "$api/actions/runs/$($run.id)/jobs?per_page=50" -Headers $headers).jobs)
        foreach ($job in $jobs | Where-Object { $_ -and $_.conclusion -and $_.conclusion -notin 'success', 'skipped' }) {
            $step = @($job.steps | Where-Object { $_.conclusion -and $_.conclusion -notin 'success', 'skipped' } | Select-Object -First 1)
            Write-Host "  $($job.name)$(if ($step) { " - step '$($step[0].name)'" }): $($job.conclusion)"
        }
        $why = if ($run.conclusion -eq 'cancelled') { ' - usually a newer push to the branch: release that commit' } else { ' - fix it, push, and release that commit' }
        throw "CI $($run.conclusion) on $short ($($run.html_url))$why. Nothing is built."
    }

    $minutes = ((Get-Date) - $started).TotalMinutes
    if (-not $run -and $minutes -gt $NoRunMinutes) {
        throw "No CI run on $short after $NoRunMinutes minutes - CI tests a commit pushed to a branch: push it, then release it. Nothing is built."
    }
    if ($minutes -gt $TimeoutMinutes) { throw "CI hasn't finished on $short within $TimeoutMinutes minutes ($($run.html_url)). Nothing is built - redo the release once it has." }
    Start-Sleep -Seconds $IntervalSeconds
}
