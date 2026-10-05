<#
.SYNOPSIS
    The Linux alternative, tested: a Windows Server 2022 VM in a container (dockur/windows, QEMU/KVM) provisioned with
    IIS, SQL Server Express and DNN - then the site is checked from the Linux host. Writes test-results/<name>/.
    Needs PowerShell 7, Docker and /dev/kvm.
#>
param([string]$Name = 'linux-vm', [string]$DnnVersion = '10.3.3', [string]$Storage = './storage')
$ErrorActionPreference = 'Stop'
$here = Split-Path $PSScriptRoot -Parent
$vm = Join-Path $here 'vm'
$out = Join-Path $here "test-results/$Name"
New-Item -ItemType Directory -Force $out | Out-Null
Import-Module (Join-Path $PSScriptRoot 'DnnHttp.psm1') -Force
$results = [System.Collections.Generic.List[object]]::new()
$facts = [ordered]@{}

$hostPassword = 'Host-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '!'
$saPassword = 'Sa-' + [Guid]::NewGuid().ToString('N').Substring(0, 16) + '!'
@{ HostUser = 'host'; HostPassword = $hostPassword; SaPassword = $saPassword; DnnVersion = $DnnVersion; Alias = 'localhost:8080' } |
    ConvertTo-Json | Set-Content (Join-Path $vm 'oem/settings.json')
function Hide([string]$text) { $text.Replace($hostPassword, '***').Replace($saPassword, '***') }

function Step([string]$name, [scriptblock]$body) {
    Write-Host "== $name"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $body
        $results.Add([pscustomobject]@{ Step = $name; Result = 'pass'; Minutes = [math]::Round($watch.Elapsed.TotalMinutes, 1); Detail = Hide ([string]($detail -join ' ')) })
        Write-Host "   pass $detail"; return $true
    }
    catch {
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Minutes = [math]::Round($watch.Elapsed.TotalMinutes, 1); Detail = Hide $_.Exception.Message })
        Write-Host "   FAIL $($_.Exception.Message)"; return $false
    }
}
function Compose([string[]]$argv) {
    $output = & docker compose --project-directory $vm -f (Join-Path $vm 'docker-compose.yml') @argv 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($argv -join ' ') failed: $(($output | Select-Object -Last 5) -join ' | ')" }
    $output
}
function Status { try { (Invoke-WebRequest 'http://localhost:8081/status.txt' -TimeoutSec 10).Content } catch { '' } }

$facts.Run = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'
$facts.Host = ((Get-Content /etc/os-release | Where-Object { $_ -like 'PRETTY_NAME=*' }) -replace 'PRETTY_NAME=|"') + ", $([Environment]::ProcessorCount) CPUs, " +
    "$([math]::Round((Get-Content /proc/meminfo | Where-Object { $_ -like 'MemTotal*' } | ForEach-Object { ($_ -split '\s+')[1] }) / 1MB, 1)) GB RAM"
$facts.Kvm = if (Test-Path /dev/kvm) { 'present' } else { 'absent' }
$facts.Guest = 'Windows Server 2022 (evaluation) via dockurr/windows, 4 vCPU, 8 GB RAM'
$env:VM_STORAGE = $Storage

$startedAt = Get-Date
$up = Step 'Start the VM container (downloads and installs Windows Server 2022)' {
    Compose @('up', '-d') | Out-Null
    'started'
}

$ready = $up -and (Step 'Windows installed and provisioned (IIS, SQL Server Express, DNN unattended install)' {
    $until = (Get-Date).AddMinutes(150)
    $seen = $false
    while ((Get-Date) -lt $until) {
        $status = Status
        if ($status -and -not $seen) { $seen = $true; $facts.WindowsReadyAfter = "$([math]::Round(((Get-Date) - $startedAt).TotalMinutes, 1)) min (first answer from the status site)" }
        if ($status -match 'READY') { return ($status -split "`n" | Where-Object { $_ } ) -join ' / ' }
        if ($status -match 'FAILED') { throw ($status -split "`n" | Where-Object { $_ } | Select-Object -Last 3) -join ' / ' }
        Start-Sleep -Seconds 30
    }
    throw "Not provisioned within 150 minutes. Last status: $(Status)"
})

if ($ready) {
    Step 'Load the home page from the Linux host (http://localhost:8080)' {
        $session = New-DnnSession 'http://localhost:8080/'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $page = Invoke-DnnRequest $session ''
        if ($page.Status -ne 200 -or $page.Html -match 'InstallWizard') {
            # DNN's log reaches the status site within a minute (provision.ps1).
            Start-Sleep -Seconds 70
            $log = try { (Invoke-WebRequest 'http://localhost:8081/dnn-log.txt' -TimeoutSec 10).Content } catch { '' }
            $errors = (($log -split "`n") | Where-Object { $_ -match '\[(ERROR|FATAL)\]|Exception' } | Select-Object -Last 4) -join ' / '
            if ($errors.Length -gt 900) { $errors = $errors.Substring(0, 900) + '…' }
            throw "HTTP $($page.Status) ($($page.Hops -join '; ')). DNN's log: $errors"
        }
        "HTTP 200 in $([math]::Round($watch.Elapsed.TotalSeconds, 1)) s"
    } | Out-Null

    $signed = Step 'Sign in as the host' {
        $script:session = New-DnnSession 'http://localhost:8080/'
        $r = Invoke-DnnSignIn $script:session 'host' $hostPassword
        if (-not $r.SignedIn) { throw $r.Detail }
        $script:homeHtml = $r.Html
        "signed in$(if ($r.PersonaBar) { ', Persona Bar shown' })"
    }

    Step 'Install an extension (Google sign-in provider, Persona Bar API)' {
        if (-not $signed) { throw 'Not signed in.' }
        $file = "Google_Auth_$(($DnnVersion.Split('.') | ForEach-Object { $_.PadLeft(2, '0') }) -join '.')_Install.resources"
        $token = Get-DnnVerificationToken $script:homeHtml
        $r = Invoke-DnnRequest $script:session 'API/PersonaBar/Extensions/InstallAvailablePackage' -Method POST `
            -Json (@{ PackageType = 'Auth_System'; FileName = $file } | ConvertTo-Json) -Headers @{ RequestVerificationToken = $token }
        if ($r.Status -ne 200) { throw "HTTP $($r.Status): $($r.Html.Substring(0, [Math]::Min(300, $r.Html.Length)))" }
        "HTTP 200: $($r.Html.Substring(0, [Math]::Min(200, $r.Html.Length)))"
    } | Out-Null

    Step 'Restart the VM container (Windows reboots) - the site comes back' {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Compose @('restart') | Out-Null
        $until = (Get-Date).AddMinutes(20)
        while ((Get-Date) -lt $until) {
            try {
                $page = Invoke-DnnRequest (New-DnnSession 'http://localhost:8080/') ''
                if ($page.Status -eq 200) { return "back after $([math]::Round($watch.Elapsed.TotalMinutes, 1)) min" }
            } catch { }
            Start-Sleep -Seconds 15
        }
        throw 'The site didn''t come back within 20 minutes.'
    } | Out-Null

    Step 'Resource use (docker stats) and the VM disk' {
        $stats = (& docker stats --no-stream --format '{{.Name}} cpu {{.CPUPerc}} mem {{.MemUsage}}') -join '; '
        $disk = (& du -sh $Storage 2>$null) -join ' '
        $facts.Stats = $stats; $facts.Disk = $disk
        "$stats | storage $disk"
    } | Out-Null
}

try { Set-Content "$out/provisioning-status.txt" (Hide (Status)) } catch { }
try { Set-Content "$out/install-output.html" (Hide (Invoke-WebRequest 'http://localhost:8081/install-output.html' -TimeoutSec 10).Content) } catch { }
try { Set-Content "$out/dnn-log.txt" (Hide (Invoke-WebRequest 'http://localhost:8081/dnn-log.txt' -TimeoutSec 10).Content) } catch { }
try { Compose @('down') | Out-Null } catch { }
Remove-Item (Join-Path $vm 'oem/settings.json') -ErrorAction SilentlyContinue

$passed = @($results | Where-Object Result -eq 'pass').Count
$facts.Summary = "$passed of $($results.Count) steps passed"
[pscustomobject]@{ Facts = $facts; Steps = $results } | ConvertTo-Json -Depth 5 | Set-Content "$out/results.json"
$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Docker DNN experiment - $Name").AppendLine()
foreach ($key in $facts.Keys) { [void]$md.AppendLine("- **$key**: $($facts[$key])") }
[void]$md.AppendLine().AppendLine('| Step | Result | Minutes | Detail |').AppendLine('|---|---|---|---|')
foreach ($r in $results) { [void]$md.AppendLine("| $($r.Step) | $($r.Result) | $($r.Minutes) | $($r.Detail.Replace('|', '/').Replace("`n", ' ')) |") }
Set-Content "$out/results.md" $md.ToString()
Write-Host $facts.Summary
