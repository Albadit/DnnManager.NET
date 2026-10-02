<#
.SYNOPSIS
    Writes the release notes for a version as Markdown.

.DESCRIPTION
    docs\release-notes\vX.Y.Z.md, when it exists, is used as it is - write it with the release-notes skill
    (.claude\skills\release-notes). Otherwise the notes are drafted in the same structure: a summary, Highlights,
    Other changes, Upgrading and Tested, from the CHANGELOG.md entry "## vX.Y.Z" (or "## Unreleased" when there is
    none), or from the commit subjects since the previous release when there is no entry at all.

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

# Notes already written in docs\release-notes are the release's notes as they are - only their relative links
# become links to the tag's files, since the GitHub release page isn't in the repository.
$saved = Join-Path $root "docs\release-notes\$Tag.md"
$target = [IO.Path]::GetFullPath($OutFile)
if ((Test-Path $saved) -and [IO.Path]::GetFullPath($saved) -ne $target) {
    $notes = [IO.File]::ReadAllText($saved)
    $notes = [regex]::Replace($notes, '\]\((?!https?:|#|mailto:)([^)\s]+)\)', {
            param($m)
            $path = [IO.Path]::GetFullPath((Join-Path (Split-Path $saved) ($m.Groups[1].Value -replace '#.*$', '')))
            $relative = $path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            $anchor = if ($m.Groups[1].Value -match '(#.*)$') { $Matches[1] } else { '' }
            "]($repoUrl/blob/$Tag/$relative$anchor)"
        })
    [IO.File]::WriteAllText($target, $notes, [Text.UTF8Encoding]::new($false))
    Write-Host "Release notes for $Tag (docs\release-notes\$Tag.md) -> $OutFile"
    return
}

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
$entryHeading = $null
$changelog = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelog) {
    $lines = [IO.File]::ReadAllLines($changelog)
    $start = -1
    # The version's own entry, or - when its heading wasn't renamed yet - the Unreleased one.
    foreach ($heading in "^## v?$([regex]::Escape($Version))(\s|$)", '^## Unreleased\s*$') {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $heading) { $start = $i; break }
        }
        if ($start -ge 0) { break }
    }
    if ($start -ge 0) {
        $entryHeading = $lines[$start]
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
        if ($end -gt $start + 1) { $entry = $lines[($start + 1)..($end - 1)] }
    }
}

# The sections every release's notes have (docs\release-notes\v1.6.0.md is the model).
$order = @('Highlights', 'Other changes', 'Upgrading', 'Tested')
$sections = [ordered]@{}
foreach ($name in $order) { $sections[$name] = [Collections.Generic.List[string]]::new() }
$fixes = [Collections.Generic.List[string]]::new()
$intro = [Collections.Generic.List[string]]::new()

if ($entry) {
    $current = $null
    foreach ($line in $entry) {
        if ($line -match '^###\s+(.+)$') {
            $current = switch -Regex ($Matches[1].Trim()) {
                '^(Added|New)' { 'Highlights'; break }
                '^Fixed' { 'Fixed'; break }
                '^(Upgrading|Installation|Update)' { 'Upgrading'; break }
                '^Tested' { 'Tested'; break }
                default { 'Other changes' }
            }
            continue
        }
        if (-not $current) { $intro.Add($line) }
        elseif ($current -eq 'Fixed') { $fixes.Add(($line -replace '^- ', '- Fixed: ')) }
        else { $sections[$current].Add($line) }
    }
}

# --- The commits since the previous release ---
$range = if ($previous) { "$previous..$head" } else { $head }
$commits = @(Invoke-Git @('log', '--no-merges', '--format=%h%x09%s', $range) | Where-Object { $_ })

if (-not $entry) {
    foreach ($c in $commits) {
        $null, $subject = $c -split "`t", 2
        # Release commits and work users don't see stay out of the notes.
        if ($subject -match '^(release|Release)\b' -or $subject -match '^(build|ci|chore|docs|test|tests|refactor|style)(\(.+\))?!?:') { continue }
        $text = $subject -replace '^[A-Za-z]+(\(.+\))?!?:\s*', ''
        if ($text.Length -gt 0) { $text = $text.Substring(0, 1).ToUpperInvariant() + $text.Substring(1) }
        if ($subject -match '^(new|feat|add)(\(.+\))?!?:') { $sections['Highlights'].Add("- $text") }
        elseif ($subject -match '^fix(\(.+\))?!?:') { $fixes.Add("- Fixed: $text") }
        else { $sections['Other changes'].Add("- $text") }
    }
}
foreach ($line in $fixes) { $sections['Other changes'].Add($line) }

# --- Markdown ---
function Get-TrimmedBlock([Collections.Generic.List[string]]$block) {
    $a = 0; $b = $block.Count - 1
    while ($a -le $b -and [string]::IsNullOrWhiteSpace($block[$a])) { $a++ }
    while ($b -ge $a -and [string]::IsNullOrWhiteSpace($block[$b])) { $b-- }
    if ($a -gt $b) { return @() }
    return $block[$a..$b]
}

$md = [Text.StringBuilder]::new()
[void]$md.AppendLine("# DNN Manager $Version").AppendLine()
$introText = @(Get-TrimmedBlock $intro)
if ($introText.Count -gt 0) { [void]$md.AppendLine(($introText -join "`n")).AppendLine() }

foreach ($name in 'Highlights', 'Other changes') {
    $body = @(Get-TrimmedBlock $sections[$name])
    if ($body.Count -gt 0) { [void]$md.AppendLine("## $name").AppendLine().AppendLine(($body -join "`n")).AppendLine() }
}

[void]$md.AppendLine('## Upgrading')
if ($previous -match '^v(\d+)\.(\d+)\.') { [void]$md.AppendLine("Install over $($Matches[1]).$($Matches[2]).x as usual.") }
$upgrade = @(Get-TrimmedBlock $sections['Upgrading'])
if ($upgrade.Count -gt 0) { [void]$md.AppendLine(($upgrade -join "`n")) }
[void]$md.AppendLine()

$tested = @(Get-TrimmedBlock $sections['Tested'])
if ($tested.Count -gt 0) { [void]$md.AppendLine('## Tested').AppendLine(($tested -join "`n")).AppendLine() }

# A draft kept in docs\release-notes links into the repository; the GitHub release links to the tag's copy.
$anchor = if ($entryHeading -match '^## v') { '#' + (($entryHeading -replace '^##\s+', '').ToLowerInvariant() -replace '[^\w\- ]', '' -replace ' ', '-') } else { '' }
$inDocs = [IO.Path]::GetDirectoryName($target) -eq [IO.Path]::GetFullPath((Join-Path $root 'docs\release-notes'))
$changelogUrl = if ($inDocs) { '../../CHANGELOG.md' } else { "$repoUrl/blob/$Tag/CHANGELOG.md" }
[void]$md.AppendLine("Full list of changes: [CHANGELOG.md]($changelogUrl$anchor)")

$text = $md.ToString().Replace("`r`n", "`n")
[IO.File]::WriteAllText($target, $text, [Text.UTF8Encoding]::new($false))
Write-Host "Release notes for $Tag ($(if ($entry) { "CHANGELOG.md: $entryHeading" } else { 'drafted from commits' }); previous release: $(if ($previous) { $previous } else { 'none' })) -> $OutFile"
