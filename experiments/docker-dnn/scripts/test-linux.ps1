<#
.SYNOPSIS
    The Linux side of the experiment: what Docker on Linux does with the Windows images DNN needs, whether SQL Server's
    container runs (and DNN's database could live there), and whether the host could run a Windows VM (KVM).
    Writes test-results\<name>\results.md and results.json. Needs PowerShell 7 and Docker.
#>
param([string]$Name = 'linux')
$ErrorActionPreference = 'Stop'
$here = Split-Path $PSScriptRoot -Parent
$out = Join-Path $here "test-results/$Name"
New-Item -ItemType Directory -Force $out | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$facts = [ordered]@{}

function Step([string]$name, [string]$expect, [scriptblock]$body) {
    Write-Host "== $name"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = & $body; $result = 'done' }
    catch { $detail = $_.Exception.Message; $result = 'error' }
    $results.Add([pscustomobject]@{ Step = $name; Expected = $expect; Result = $result; Seconds = [math]::Round($watch.Elapsed.TotalSeconds); Detail = ([string]($detail -join ' ')) })
    Write-Host "   $result - $detail"
}
function Run([string]$exe, [string[]]$argv) {
    $output = & $exe @argv 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($output -join "`n") }
}

$facts.Run = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'
$facts.Host = (Get-Content /etc/os-release | Where-Object { $_ -like 'PRETTY_NAME=*' }) -replace 'PRETTY_NAME=|"'
$facts.Kernel = (Run uname @('-sr')).Text
$facts.Docker = (Run docker @('version', '--format', 'server {{.Server.Version}} ({{.Server.Os}}/{{.Server.Arch}})')).Text

Step 'Pull the Windows image DNN needs (aspnet 4.8.1, Server Core ltsc2022)' 'fails: no linux/amd64 variant' {
    $r = Run docker @('pull', 'mcr.microsoft.com/dotnet/framework/aspnet:4.8.1-windowsservercore-ltsc2022')
    if ($r.Code -eq 0) { throw 'It pulled - unexpected on Linux.' }
    $facts.WindowsImageOnLinux = 'refused'
    "exit $($r.Code): $(($r.Text -split "`n" | Select-Object -Last 2) -join ' ')"
}

Step 'Ask for the Windows platform explicitly (--platform windows/amd64)' 'fails: the Linux kernel can''t run it' {
    $r = Run docker @('pull', '--platform', 'windows/amd64', 'mcr.microsoft.com/windows/nanoserver:ltsc2022')
    if ($r.Code -eq 0) {
        $run = Run docker @('run', '--rm', '--platform', 'windows/amd64', 'mcr.microsoft.com/windows/nanoserver:ltsc2022', 'cmd', '/c', 'ver')
        "pull worked; run: exit $($run.Code): $(($run.Text -split "`n" | Select-Object -Last 2) -join ' ')"
    }
    else { "exit $($r.Code): $(($r.Text -split "`n" | Select-Object -Last 2) -join ' ')" }
}

Step 'SQL Server 2022 in a Linux container (what DNN''s database could use on Linux/macOS)' 'works' {
    $password = 'Sa-' + [Guid]::NewGuid().ToString('N').Substring(0, 16) + '!'
    $r = Run docker @('run', '-d', '--name', 'mssql', '-e', 'ACCEPT_EULA=Y', '-e', "MSSQL_SA_PASSWORD=$password", '-p', '14330:1433', 'mcr.microsoft.com/mssql/server:2022-latest')
    if ($r.Code -ne 0) { throw $r.Text }
    try {
        for ($i = 0; $i -lt 60; $i++) {
            $q = Run docker @('exec', 'mssql', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', $password, '-h', '-1', '-Q', 'SET NOCOUNT ON; SELECT @@VERSION')
            if ($q.Code -eq 0) { return ($q.Text -split "`n" | Select-Object -First 1).Trim() }
            Start-Sleep -Seconds 3
        }
        throw 'SQL Server did not answer within 3 minutes.'
    }
    finally { Run docker @('rm', '-f', 'mssql') | Out-Null }
}

Step 'Hardware virtualization for a Windows VM (/dev/kvm)' 'present on hosts that could run Windows in QEMU/KVM' {
    $kvm = Test-Path /dev/kvm
    $cpu = (Get-Content /proc/cpuinfo | Where-Object { $_ -match '^flags' } | Select-Object -First 1)
    $facts.Kvm = if ($kvm) { 'present' } else { 'absent' }
    "/dev/kvm $(if ($kvm) { 'present' } else { 'absent' }); CPU flags vmx/svm: $(if ($cpu -match '\b(vmx|svm)\b') { $Matches[1] } else { 'none' })"
}

[pscustomobject]@{ Facts = $facts; Steps = $results } | ConvertTo-Json -Depth 5 | Set-Content "$out/results.json"
$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Docker DNN experiment - $Name").AppendLine()
foreach ($key in $facts.Keys) { [void]$md.AppendLine("- **$key**: $($facts[$key])") }
[void]$md.AppendLine().AppendLine('| Step | Expected | Result | Seconds | Detail |').AppendLine('|---|---|---|---|---|')
foreach ($r in $results) { [void]$md.AppendLine("| $($r.Step) | $($r.Expected) | $($r.Result) | $($r.Seconds) | $($r.Detail.Replace('|', '/').Replace("`n", ' ')) |") }
Set-Content "$out/results.md" $md.ToString()
