<#
.SYNOPSIS
    Writes the release notes for a version as Markdown.

.DESCRIPTION
    The hand-written entry in CHANGELOG.md comes first: its "## vX.Y.Z" section is sorted into What's new,
    Improvements, Bug fixes, Security, Removed and Installation / Update notes. Without an entry, the same
    sections are made from the commit subjects since the previous release (new: / fix: / update: ...).
    The commits since the previous release are always listed at the end.

.EXAMPLE
    .github\scripts\release-notes.ps1 -Version 1.7.0 -OutFile release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutFile,
    # The tag being released; defaults to v<Version>. HEAD is used when the tag doesn't exist yet.
    [string]$Tag,
    [string]$Repository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Tag) { $Tag = "v$Version" }
if (-not $Repository) { $Repository = 'Bond-for-web-solutions/DnnManager.NET' }
$repoUrl = "https://github.com/$Repository"

function Invoke-Git([string[]]$argv) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = & git -C $root @argv 2>$null } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) { return $null }
    return $out
}

$head = if (Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/tags/$Tag")) { $Tag } else { 'HEAD' }
# The previous release: the newest vX.Y.Z tag that is an ancestor of this one.
$previous = Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*.[0-9]*.[0-9]*', "$head^")

# --- The CHANGELOG.md entry ---
$entry = $null
$changelog = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelog) {
    $lines = [IO.File]::ReadAllLines($changelog)
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^## v?$([regex]::Escape($Version))(\s|$)") { $start = $i; break }
    }
    if ($start -ge 0) {
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
        if ($end -gt $start + 1) { $entry = $lines[($start + 1)..($end - 1)] }
    }
}

# Release-note section for each CHANGELOG heading and commit prefix.
$order = @("What's new", 'Improvements', 'Bug fixes', 'Security', 'Removed', 'Technical changes', 'Installation / Update notes')
$sections = [ordered]@{}
foreach ($name in $order) { $sections[$name] = [Collections.Generic.List[string]]::new() }
$intro = [Collections.Generic.List[string]]::new()

function SectionForHeading([string]$heading) {
    switch -Regex ($heading) {
        '^(Added|New)' { return "What's new" }
        '^(Changed|Improved|Performance)' { return 'Improvements' }
        '^Fixed' { return 'Bug fixes' }
        '^Security' { return 'Security' }
        '^(Removed|Deprecated|Breaking)' { return 'Removed' }
        '^(Upgrading|Installation|Update)' { return 'Installation / Update notes' }
        default { return 'Technical changes' }
    }
}

if ($entry) {
    $current = $null
    foreach ($line in $entry) {
        if ($line -match '^###\s+(.+)$') { $current = SectionForHeading $Matches[1].Trim(); continue }
        if ($current) { $sections[$current].Add($line) } else { $intro.Add($line) }
    }
}

# --- The commits since the previous release ---
$range = if ($previous) { "$previous..$head" } else { $head }
$commits = @(Invoke-Git @('log', '--no-merges', '--format=%h%x09%s', $range) | Where-Object { $_ })
$commitLines = foreach ($c in $commits) {
    $hash, $subject = $c -split "`t", 2
    if ($subject -match '^(release|Release)\b') { continue }
    [pscustomobject]@{ Hash = $hash; Subject = $subject }
}

if (-not $entry) {
    foreach ($c in $commitLines) {
        $section = switch -Regex ($c.Subject) {
            '^(new|feat|add)(\(.+\))?!?:' { "What's new"; break }
            '^fix(\(.+\))?!?:' { 'Bug fixes'; break }
            '^(sec|security)(\(.+\))?!?:' { 'Security'; break }
            '^(remove|revert)(\(.+\))?!?:' { 'Removed'; break }
            '^(build|ci|chore|docs|test|tests|refactor|style)(\(.+\))?!?:' { 'Technical changes'; break }
            default { 'Improvements' }
        }
        $text = $c.Subject -replace '^[A-Za-z]+(\(.+\))?!?:\s*', ''
        if ($text.Length -gt 0) { $text = $text.Substring(0, 1).ToUpperInvariant() + $text.Substring(1) }
        $sections[$section].Add("- $text ($($c.Hash))")
    }
}

# --- Markdown ---
function Trim-Block([Collections.Generic.List[string]]$block) {
    $a = 0; $b = $block.Count - 1
    while ($a -le $b -and [string]::IsNullOrWhiteSpace($block[$a])) { $a++ }
    while ($b -ge $a -and [string]::IsNullOrWhiteSpace($block[$b])) { $b-- }
    if ($a -gt $b) { return @() }
    return $block[$a..$b]
}

$md = [Text.StringBuilder]::new()
[void]$md.AppendLine("# DNN Manager $Version").AppendLine()
$introText = @(Trim-Block $intro)
if ($introText.Count -gt 0) { [void]$md.AppendLine(($introText -join "`n")).AppendLine() }

foreach ($name in $order) {
    if ($name -eq 'Installation / Update notes') { continue }
    $body = @(Trim-Block $sections[$name])
    if ($body.Count -eq 0) { continue }
    [void]$md.AppendLine("## $name").AppendLine().AppendLine(($body -join "`n")).AppendLine()
}

[void]$md.AppendLine('## Installation / Update notes').AppendLine()
$upgrade = @(Trim-Block $sections['Installation / Update notes'])
if ($upgrade.Count -gt 0) { [void]$md.AppendLine(($upgrade -join "`n")).AppendLine() }
[void]$md.AppendLine("- **Installer:** run ``DnnManagerSetup-$Version-x64.exe``. It installs per user (no administrator rights) and upgrades an earlier install in place; your settings in ``Documents\DnnManager`` are kept.")
[void]$md.AppendLine("- **Portable:** ``DnnManager-$Version-x64.exe`` is a single self-contained file - no .NET install needed. Run it as Administrator, since it manages IIS.")
[void]$md.AppendLine('- `SHA256SUMS.txt` holds the checksums of the files above.').AppendLine()

$listed = @($commitLines)
if ($listed.Count -gt 0) {
    [void]$md.AppendLine('<details>').AppendLine("<summary>Commits since $(if ($previous) { $previous } else { 'the first commit' }) ($($listed.Count))</summary>").AppendLine()
    foreach ($c in $listed) { [void]$md.AppendLine("- $($c.Subject) ($($c.Hash))") }
    [void]$md.AppendLine().AppendLine('</details>').AppendLine()
}

if ($previous) { [void]$md.AppendLine("**Full changelog:** [$previous...$Tag]($repoUrl/compare/$previous...$Tag) | [CHANGELOG.md]($repoUrl/blob/$Tag/CHANGELOG.md)") }
else { [void]$md.AppendLine("**Full changelog:** [CHANGELOG.md]($repoUrl/blob/$Tag/CHANGELOG.md)") }

$text = $md.ToString().Replace("`r`n", "`n")
[IO.File]::WriteAllText($OutFile, $text, [Text.UTF8Encoding]::new($false))
Write-Host "Release notes for $Tag ($(if ($entry) { 'CHANGELOG.md entry' } else { 'generated from commits' }); previous release: $(if ($previous) { $previous } else { 'none' })) -> $OutFile"
