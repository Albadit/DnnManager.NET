<#
.SYNOPSIS
    What the release workflow builds for a version tag, and how GitHub labels the release - read from the tag's
    message. Run by the "Plan" job of .github\workflows\release.yml, not by hand.

.DESCRIPTION
    publish-release.ps1 (the VS Code task "Release") writes the label and the platforms you chose into the
    annotated tag's message (ConvertTo-ReleaseTagMessage in ReleaseCommon.psm1). This reads the tag back from
    GitHub's API - the check-out may hold the tag without its message - and writes the job's outputs:

      version        2.0.0-rc1           the tag without the v
      file-version   2.0.0.0             its numbers, for the exes' file version
      label          latest | prerelease | none
      windows        true | false        build Setup and the portable exe
      desktop        true | false        build the cross-platform app's packages
      targets        JSON                the Desktop workflow's matrix: the macOS and Linux runtimes chosen
      assets         windows macos linux the platforms, for upload-release.ps1

    A tag without the options (a lightweight one, or one made by hand): every platform, and the label its version
    gives - a vX.Y.Z-suffix tag a pre-release, any other the latest. Options that can't be released (a pre-release's
    version as the latest, the latest without Windows) fail the job, before anything is built.

.EXAMPLE
    .github\scripts\release-plan.ps1 -Tag v2.0.0-rc1 -Message "Release-Label: prerelease`nRelease-Platforms: macos"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    # The tag's message, instead of asking GitHub for it (to try this out).
    [string]$Message,
    # Where the outputs go: the job's GITHUB_OUTPUT; printed when there is none.
    [string]$OutFile = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleaseCommon.psm1') -Force

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "$Tag isn't a version tag like v1.9.0 or v2.0.0-rc1." }
$fileVersion = "$($Matches[1]).0"

# --- The tag's message, from GitHub: an annotated tag's object holds it; a lightweight tag points at the commit ---

if (-not $PSBoundParameters.ContainsKey('Message')) {
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "$Repository isn't a repository (owner/name)." }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'DnnManager-release' }
    if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
    $api = "https://api.github.com/repos/$Repository"
    $object = (Invoke-RestMethod -Uri "$api/git/ref/tags/$Tag" -Headers $headers).object
    $Message = if ($object.type -eq 'tag') { [string](Invoke-RestMethod -Uri "$api/git/tags/$($object.sha)" -Headers $headers).message } else { '' }
    if (-not $Message) { Write-Host "$Tag has no message with the release's options - every platform, and the label its version gives." }
}

$options = ConvertFrom-ReleaseTagMessage $Tag $Message
if ($problem = Get-ReleaseOptionsProblem $Tag $options.Label $options.Platforms) { throw "$problem (from the tag's message) - nothing was built." }

# The Desktop workflow's runners for the macOS and Linux packages (.github\workflows\desktop.yml).
$runtimes = @{
    macos = @(@{ os = 'macos-15'; rid = 'osx-arm64'; name = 'macos-arm64' }, @{ os = 'macos-15'; rid = 'osx-x64'; name = 'macos-x64' })
    linux = @(@{ os = 'ubuntu-24.04'; rid = 'linux-x64'; name = 'linux-x64' }, @{ os = 'ubuntu-24.04'; rid = 'linux-arm64'; name = 'linux-arm64' })
}
$targets = @($options.Platforms | Where-Object { $runtimes.ContainsKey($_) } | ForEach-Object { $runtimes[$_] })

$outputs = [ordered]@{
    'version'      = $Tag.Substring(1)
    'file-version' = $fileVersion
    'label'        = $options.Label
    'windows'      = ('windows' -in $options.Platforms).ToString().ToLowerInvariant()
    'desktop'      = ($targets.Count -gt 0).ToString().ToLowerInvariant()
    'targets'      = ConvertTo-Json -InputObject @($targets) -Compress
    'assets'       = $options.Platforms -join ' '
}
$lines = $outputs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
$lines | ForEach-Object { Write-Host $_ }
if ($OutFile) { [IO.File]::AppendAllLines($OutFile, [string[]]$lines) }
