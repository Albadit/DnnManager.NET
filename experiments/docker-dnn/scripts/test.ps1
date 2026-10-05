<#
.SYNOPSIS
    Runs the whole experiment on a host with a Windows-containers Docker engine and writes what happened to
    test-results\<name>\ (results.md, results.json, logs).

.DESCRIPTION
    Builds the images, starts SQL Server + DNN, installs DNN unattended, then checks: the home page, signing in as the
    host, installing an extension, a restart, recreating the containers (volumes kept), a second project next to the
    first, and a file changed on a bind-mounted folder. Each step is timed; a failed step is recorded and the next
    ones still run where they can. Needs PowerShell 7 and Docker Compose v2.

.EXAMPLE
    pwsh -File scripts\test.ps1 -Name windows-2022
#>
param(
    [string]$Name = 'local',
    [string]$DnnVersion = '10.3.3',
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$here = Split-Path $PSScriptRoot -Parent
Set-Location $here
Import-Module (Join-Path $PSScriptRoot 'DnnHttp.psm1') -Force

$out = Join-Path $here "test-results\$Name"
New-Item -ItemType Directory -Force $out, "$out\logs" | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$facts = [ordered]@{}

# One password for this run - never written to the results.
$env:SA_PASSWORD = 'Sa-' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18)).Replace('+', 'x').Replace('/', 'y')
$env:DNN_VERSION = $DnnVersion
$hostUser = 'host'
$hostPassword = 'Host-' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(12)).Replace('+', 'x').Replace('/', 'y')
function Hide([string]$text) { $text.Replace($env:SA_PASSWORD, '***').Replace($hostPassword, '***') }

function Step([string]$name, [scriptblock]$body) {
    Write-Host ''
    Write-Host "== $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $body
        $results.Add([pscustomobject]@{ Step = $name; Result = 'pass'; Seconds = [math]::Round($watch.Elapsed.TotalSeconds); Detail = Hide ([string]($detail -join ' ')) })
        Write-Host "   pass ($([math]::Round($watch.Elapsed.TotalSeconds)) s) $detail" -ForegroundColor Green
        return $true
    }
    catch {
        $message = Hide $_.Exception.Message
        $results.Add([pscustomobject]@{ Step = $name; Result = 'FAIL'; Seconds = [math]::Round($watch.Elapsed.TotalSeconds); Detail = $message })
        Write-Host "   FAIL: $message" -ForegroundColor Red
        return $false
    }
}

function Docker([string[]]$argv, [switch]$Quiet) {
    $output = & docker @argv 2>&1 | ForEach-Object { "$_" }
    if (-not $Quiet) { $output | ForEach-Object { Write-Host "   $(Hide $_)" } }
    if ($LASTEXITCODE -ne 0) { throw "docker $($argv -join ' ') failed: $(Hide (($output | Select-Object -Last 5) -join ' | '))" }
    return $output
}

# The site's base URL from the host: published ports on localhost - or, where Windows NAT doesn't loop published ports
# back to the host, the container's own address (the Host header stays the alias).
function Wait-Site([string]$service, [int]$port, [int]$seconds = 600) {
    $until = (Get-Date).AddSeconds($seconds)
    $container = (Docker @('compose', 'ps', '-q', $service) -Quiet | Select-Object -First 1)
    while ((Get-Date) -lt $until) {
        foreach ($base in @("http://localhost:$port/")) {
            try {
                $r = Invoke-WebRequest $base -MaximumRedirection 0 -SkipHttpErrorCheck -TimeoutSec 30 -ErrorAction Stop
                if ($r.StatusCode -ne 503) { return $base }
            }
            catch { }
        }
        $state = (Docker @('inspect', '-f', '{{.State.Status}}', $container) -Quiet)
        if ($state -ne 'running') { throw "The $service container is $state." }
        Start-Sleep -Seconds 5
    }
    throw "Nothing answered on http://localhost:$port within $seconds s."
}

function Install-Dnn([string]$service, [int]$port) {
    $alias = "localhost:$port"
    Docker @('compose', 'exec', '-T', $service, 'powershell', '-NoProfile', '-File', 'C:\scripts\write-install-template.ps1',
        '-Alias', $alias, '-HostUser', $hostUser, '-HostPassword', $hostPassword) | Out-Null
    $session = New-DnnSession "http://localhost:$port/"
    # DNN writes new machine keys, restarts and redirects to itself: Invoke-DnnRequest follows that.
    $page = Invoke-DnnRequest $session 'Install/Install.aspx?mode=install'
    Set-Content "$out\logs\install-$service.html" (Hide $page.Html)
    $after = Docker @('compose', 'exec', '-T', $service, 'powershell', '-NoProfile', '-File', 'C:\scripts\after-install.ps1', '-HostUser', $hostUser) -Quiet
    $version = ($after | Where-Object { $_ -like 'version=*' }) -replace 'version='
    $hosts = ($after | Where-Object { $_ -like 'hosts=*' }) -replace 'hosts='
    if ($page.Status -ne 200) { throw "Install.aspx answered HTTP $($page.Status) ($($page.Hops -join '; '))." }
    if ($page.Html -notmatch 'Successfully Installed Site|Installation Complete') { throw "DNN's install output doesn't say it completed - see logs\install-$service.html." }
    if ($version -ne $DnnVersion) { throw "The database says DNN '$version', expected $DnnVersion." }
    if ($hosts -ne '1') { throw "The host account '$hostUser' isn't in the database." }
    "DNN $version installed for $alias"
}

function Test-Home([int]$port) {
    $session = New-DnnSession "http://localhost:$port/"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $page = Invoke-DnnRequest $session ''
    $first = $watch.Elapsed.TotalSeconds
    if ($page.Status -ne 200) { throw "The home page answered HTTP $($page.Status) ($($page.Hops -join '; '))." }
    if ($page.Uri.AbsolutePath -like '/Install*' -or $page.Html -match 'InstallWizard') { throw 'The site still sends visitors to DNN''s installer.' }
    $watch.Restart()
    [void](Invoke-DnnRequest $session '')
    "HTTP 200, first request $([math]::Round($first, 1)) s, next $([math]::Round($watch.Elapsed.TotalSeconds, 2)) s"
}

function Test-SignIn([int]$port) {
    $session = New-DnnSession "http://localhost:$port/"
    $signIn = Invoke-DnnSignIn $session $hostUser $hostPassword
    if (-not $signIn.SignedIn) { throw "Not signed in: $($signIn.Detail)" }
    $script:lastSession = $session
    $script:lastHome = $signIn.Html
    "signed in as $hostUser$(if ($signIn.PersonaBar) { ', Persona Bar shown' })"
}

function Get-Count([string]$service, [string]$query) {
    [int]((Docker @('compose', 'exec', '-T', $service, 'powershell', '-NoProfile', '-File', 'C:\scripts\query.ps1', '-Query', $query) -Quiet) | Select-Object -Last 1)
}

# ─── The environment ───────────────────────────────────────────────────

$facts.Run = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'
$facts.Host = "$([Environment]::OSVersion.VersionString), $(Get-CimInstance Win32_OperatingSystem | ForEach-Object Caption)"
$facts.Cpu = "$((Get-CimInstance Win32_Processor | Select-Object -First 1).Name), $([Environment]::ProcessorCount) logical"
$facts.Memory = "$([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) GB"
$facts.Docker = (Docker @('version', '--format', 'client {{.Client.Version}}, server {{.Server.Version}} ({{.Server.Os}}/{{.Server.Arch}})') -Quiet) -join ' '
$facts.Isolation = (Docker @('info', '--format', '{{.Isolation}}') -Quiet) -join ' '
$facts.Compose = (Docker @('compose', 'version', '--short') -Quiet) -join ' '
$facts.DnnVersion = $DnnVersion

# ─── The steps ─────────────────────────────────────────────────────────

$built = Step 'Build the images (SQL Server Express, DNN)' {
    Docker @('compose', 'build', 'sql', 'web') | Out-Null
    (Docker @('images', '--format', '{{.Repository}}:{{.Tag}} {{.Size}}', 'dnn-docker/*') -Quiet) -join '; '
}
if (-not $built) { $facts.Stopped = 'The images could not be built.' }

$up = $built -and (Step 'Start SQL Server and DNN (first start: DNN copied into the site volume, database created)' {
    Docker @('compose', 'up', '-d', 'sql', 'web') | Out-Null
    $base = Wait-Site 'web' 8080
    "answering on $base"
})

$installed = $up -and (Step 'Install DNN unattended (Install.aspx?mode=install with an install template)' { Install-Dnn 'web' 8080 })
if ($installed) {
    Step 'Load the home page' { Test-Home 8080 } | Out-Null
    $signed = Step 'Sign in as the host (superuser)' { Test-SignIn 8080 }

    Step 'Install an extension (Google sign-in provider, through the Persona Bar API)' {
        $file = "Google_Auth_$(($DnnVersion.Split('.') | ForEach-Object { $_.PadLeft(2, '0') }) -join '.')_Install.resources"
        $before = Get-Count 'web' "SELECT COUNT(*) FROM dbo.Packages WHERE PackageType = 'Auth_System' AND Name LIKE '%Google%'"
        if (-not $signed) { throw 'Not signed in.' }
        $token = Get-DnnVerificationToken $script:lastHome
        if (-not $token) { throw 'No anti-forgery token on the home page.' }
        $body = @{ PackageType = 'Auth_System'; FileName = $file } | ConvertTo-Json
        $r = Invoke-DnnRequest $script:lastSession 'API/PersonaBar/Extensions/InstallAvailablePackage' -Method POST -Json $body -Headers @{ RequestVerificationToken = $token }
        Set-Content "$out\logs\install-extension-response.txt" "HTTP $($r.Status)`n$($r.Html)"
        $after = Get-Count 'web' "SELECT COUNT(*) FROM dbo.Packages WHERE PackageType = 'Auth_System' AND Name LIKE '%Google%'"
        if ($after -le $before) { throw "Not installed: the API answered HTTP $($r.Status): $($r.Html.Substring(0, [Math]::Min(300, $r.Html.Length)))" }
        "$file installed (HTTP $($r.Status)); Google packages $before -> $after"
    } | Out-Null

    Step 'Restart the DNN container - the site comes back' {
        Docker @('compose', 'restart', 'web') | Out-Null
        [void](Wait-Site 'web' 8080)
        (Test-Home 8080) + '; ' + (Test-SignIn 8080)
    } | Out-Null

    Step 'Recreate both containers (docker compose down + up) - the volumes keep the site and the database' {
        Docker @('compose', 'down') | Out-Null
        Docker @('compose', 'up', '-d', 'sql', 'web') | Out-Null
        [void](Wait-Site 'web' 8080)
        $google = Get-Count 'web' "SELECT COUNT(*) FROM dbo.Packages WHERE PackageType = 'Auth_System' AND Name LIKE '%Google%'"
        if ($google -lt 1) { throw 'The installed extension is gone.' }
        (Test-Home 8080) + '; ' + (Test-SignIn 8080) + '; the extension is still installed'
    } | Out-Null

    Step 'A second DNN project next to the first (own site volume and database, port 8081)' {
        Docker @('compose', '--profile', 'second', 'up', '-d', 'web2') | Out-Null
        [void](Wait-Site 'web2' 8081)
        $install = Install-Dnn 'web2' 8081
        "$install; " + (Test-Home 8081) + '; ' + (Test-SignIn 8081) + '; first project still: ' + (Test-Home 8080)
    } | Out-Null

    Step 'Resource use (docker stats) and disk (images, volumes)' {
        $stats = Docker @('stats', '--no-stream', '--format', '{{.Name}} cpu {{.CPUPerc}} mem {{.MemUsage}}') -Quiet
        $df = Docker @('system', 'df', '--format', '{{.Type}} {{.Size}}') -Quiet
        $facts.Stats = $stats -join '; '
        $facts.Disk = $df -join '; '
        "$($stats -join '; ') | $($df -join '; ')"
    } | Out-Null
}

# A host folder bind-mounted as the site: does IIS/ASP.NET see a file changed from the host? (The DNN community reports
# it doesn't.) A plain ASP.NET app - no DNN or database needed.
Step 'File change notifications on a bind-mounted host folder (web.config edited from the host)' {
    $folder = Join-Path $env:RUNNER_TEMP 'bind-site'
    if (-not $env:RUNNER_TEMP) { $folder = Join-Path ([IO.Path]::GetTempPath()) 'bind-site' }
    New-Item -ItemType Directory -Force $folder | Out-Null
    # Process isolation checks the host's ACLs for the container's accounts.
    icacls $folder /grant '*S-1-5-11:(OI)(CI)RX' | Out-Null
    Set-Content "$folder\default.aspx" '<%@ Page Language="C#" %><%= System.Configuration.ConfigurationManager.AppSettings["marker"] %>|<%= System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("o") %>'
    Set-Content "$folder\web.config" '<configuration><appSettings><add key="marker" value="one" /></appSettings><system.web><compilation targetFramework="4.8" /></system.web></configuration>'
    Docker @('run', '-d', '--name', 'bind-test', '-p', '8090:80', '-v', "$($folder):C:\inetpub\wwwroot", '--entrypoint', 'C:\ServiceMonitor.exe',
        'mcr.microsoft.com/dotnet/framework/aspnet:4.8.1-windowsservercore-ltsc2022', 'w3svc') -Quiet | Out-Null
    try {
        $first = $null
        for ($i = 0; $i -lt 60 -and -not $first; $i++) {
            try { $first = (Invoke-WebRequest 'http://localhost:8090/default.aspx' -TimeoutSec 30).Content.Trim() } catch { Start-Sleep 3 }
        }
        if ($first -notlike 'one|*') { throw "The test page answered '$first'." }
        Set-Content "$folder\web.config" '<configuration><appSettings><add key="marker" value="two" /></appSettings><system.web><compilation targetFramework="4.8" /></system.web></configuration>'
        Start-Sleep -Seconds 10
        $second = (Invoke-WebRequest 'http://localhost:8090/default.aspx' -TimeoutSec 60).Content.Trim()
        $facts.BindMountChangeNotification = if ($second -like 'two|*') { 'seen' } else { 'NOT seen' }
        if ($second -notlike 'two|*') { throw "ASP.NET didn't see the change (still '$second') - the app has to be restarted by hand." }
        "web.config changed on the host -> ASP.NET restarted the app ('$first' -> '$second')"
    }
    finally { Docker @('rm', '-f', 'bind-test') -Quiet | Out-Null }
} | Out-Null

# ─── Logs and results ──────────────────────────────────────────────────

foreach ($service in 'sql', 'web', 'web2') {
    try { Set-Content "$out\logs\$service.log" (Hide ((Docker @('compose', '--profile', 'second', 'logs', '--no-color', $service) -Quiet) -join "`n")) } catch { }
}
try {
    $dnnLogs = Docker @('compose', 'exec', '-T', 'web', 'powershell', '-NoProfile', '-File', 'C:\scripts\dnn-logs.ps1') -Quiet
    Set-Content "$out\logs\dnn-logs.txt" (Hide ($dnnLogs -join "`n"))
} catch { }

if (-not $KeepRunning) { try { Docker @('compose', '--profile', 'second', 'down', '-v') -Quiet | Out-Null } catch { } }

$passed = @($results | Where-Object Result -eq 'pass').Count
$facts.Summary = "$passed of $($results.Count) steps passed"
[pscustomobject]@{ Facts = $facts; Steps = $results } | ConvertTo-Json -Depth 5 | Set-Content "$out\results.json"

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Docker DNN experiment - $Name").AppendLine()
foreach ($key in $facts.Keys) { [void]$md.AppendLine("- **$key**: $($facts[$key])") }
[void]$md.AppendLine().AppendLine('| Step | Result | Seconds | Detail |').AppendLine('|---|---|---|---|')
foreach ($r in $results) { [void]$md.AppendLine("| $($r.Step) | $($r.Result) | $($r.Seconds) | $($r.Detail.Replace('|', '/')) |") }
Set-Content "$out\results.md" $md.ToString()
Write-Host ''
Write-Host $facts.Summary
