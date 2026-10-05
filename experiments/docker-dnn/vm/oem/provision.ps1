# Provisions the Windows Server VM for DNN, at the end of Windows setup (dockur/windows runs C:\OEM\install.bat):
# IIS with ASP.NET 4.8, SQL Server 2022 Express, DNN in C:\inetpub\dnn, and DNN's unattended install - then the host
# isn't made to change its password at the first sign-in. Progress is served on port 8081 (status.txt), so the Linux
# host can follow it; the last line is READY or FAILED: <reason>.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$settings = Get-Content C:\OEM\settings.json -Raw | ConvertFrom-Json
$statusDir = 'C:\status'
New-Item -ItemType Directory -Force $statusDir | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
function Status([string]$text) {
    $line = "{0,5:N0} s  {1}" -f $watch.Elapsed.TotalSeconds, $text
    Add-Content "$statusDir\status.txt" $line
    Write-Host $line
}

try {
    Status 'Installing IIS with ASP.NET 4.8'
    Install-WindowsFeature Web-Server, Web-Asp-Net45, Web-Default-Doc, Web-Static-Content, Web-Http-Errors | Out-Null
    $appcmd = 'C:\Windows\System32\inetsrv\appcmd.exe'
    # The status site, so the host sees how far this got.
    & $appcmd add site /name:status /bindings:http/*:8081: /physicalPath:$statusDir | Out-Null
    New-NetFirewallRule -DisplayName 'DNN dev (80, 8081)' -Direction Inbound -Protocol TCP -LocalPort 80, 8081 -Action Allow | Out-Null
    Status 'IIS is up'

    Status 'Installing SQL Server 2022 Express'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?linkid=2216019' -OutFile C:\ssei.exe -UseBasicParsing
    $p = Start-Process C:\ssei.exe -ArgumentList '/ACTION=Download', '/MEDIAPATH=C:\setup', '/MEDIATYPE=Core', '/QUIET' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "SQL Server Express download failed ($($p.ExitCode))" }
    $media = Get-ChildItem C:\setup -Filter *.exe | Select-Object -First 1
    $p = Start-Process $media.FullName -ArgumentList '/q', '/x:C:\setup\sql' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Unpacking $($media.Name) failed ($($p.ExitCode))" }
    $p = Start-Process C:\setup\sql\setup.exe -Wait -PassThru -NoNewWindow -ArgumentList '/q', '/ACTION=Install', '/FEATURES=SQLEngine',
        '/INSTANCENAME=SQLEXPRESS', '/UPDATEENABLED=0', '/SECURITYMODE=SQL', "/SAPWD=`"$($settings.SaPassword)`"",
        '/SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"', '/SQLSYSADMINACCOUNTS="BUILTIN\ADMINISTRATORS"', '/TCPENABLED=1', '/IACCEPTSQLSERVERLICENSETERMS'
    if ($p.ExitCode -ne 0) { throw "SQL Server setup failed ($($p.ExitCode))" }
    Status 'SQL Server Express is up'

    $db = "Data Source=.\SQLEXPRESS;Initial Catalog=dnn;User ID=sa;Password=$($settings.SaPassword);"
    function Sql([string]$connection, [string]$query) {
        $c = New-Object System.Data.SqlClient.SqlConnection $connection
        try { $c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = $query; $cmd.CommandTimeout = 120; $cmd.ExecuteScalar() } finally { $c.Dispose() }
    }
    Sql "Data Source=.\SQLEXPRESS;Initial Catalog=master;User ID=sa;Password=$($settings.SaPassword);" 'CREATE DATABASE [dnn]' | Out-Null

    Status "Downloading DNN $($settings.DnnVersion)"
    $site = 'C:\inetpub\dnn'
    New-Item -ItemType Directory -Force $site | Out-Null
    icacls $site /grant 'IIS AppPool\DefaultAppPool:(OI)(CI)M' | Out-Null
    Invoke-WebRequest "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v$($settings.DnnVersion)/DNN_Platform_$($settings.DnnVersion)_Install.zip" -OutFile C:\dnn.zip -UseBasicParsing
    Expand-Archive C:\dnn.zip $site
    [xml]$config = Get-Content "$site\web.config" -Raw
    foreach ($add in $config.configuration.connectionStrings.add) { if ($add.name -eq 'SiteSqlServer') { $add.connectionString = $db } }
    $config.Save("$site\web.config")
    & $appcmd set vdir 'Default Web Site/' /physicalPath:$site | Out-Null
    & $appcmd set apppool DefaultAppPool /processModel.idleTimeout:00:00:00 | Out-Null

    # DNN's install template - the host account and the alias the Linux host's browser uses. Nodes through
    # SelectSingleNode, not PowerShell's XML properties: an empty or missing element comes back as a string or $null.
    [xml]$doc = Get-Content "$site\Install\DotNetNuke.install.config.resources" -Raw
    function Get-Child($parent, [string]$name) {
        $node = $parent.SelectSingleNode($name)
        if (-not $node) { $node = $parent.AppendChild($doc.CreateElement($name)) }
        return $node
    }
    function Set-Child($parent, [string]$name, [string]$value) { (Get-Child $parent $name).InnerText = $value }
    $root = $doc.DocumentElement
    $superuser = Get-Child $root 'superuser'
    Set-Child $superuser 'username' $settings.HostUser
    Set-Child $superuser 'password' $settings.HostPassword
    Set-Child $superuser 'updatepassword' 'false'
    foreach ($connection in @($root.SelectNodes('connection'))) { [void]$root.RemoveChild($connection) }
    $portal = (Get-Child $root 'portals').SelectSingleNode('portal')
    Set-Child (Get-Child $portal 'administrator') 'password' ([Guid]::NewGuid().ToString('N') + 'Aa1!')
    $aliases = Get-Child $portal 'portalaliases'
    $aliases.RemoveAll()
    Set-Child $aliases 'portalalias' $settings.Alias
    $doc.Save("$site\Install\DotNetNuke.install.config")

    Status 'Running DNN''s unattended install'
    $path = '/Install/Install.aspx?mode=install'
    for ($i = 0; $i -lt 6; $i++) {
        $request = [Net.HttpWebRequest]::Create("http://127.0.0.1$path")
        $request.Host = $settings.Alias
        $request.AllowAutoRedirect = $false
        $request.Timeout = 1800000
        $request.ReadWriteTimeout = 1800000
        try { $response = $request.GetResponse() } catch [Net.WebException] { $response = $_.Exception.Response; if (-not $response) { throw } }
        $code = [int]$response.StatusCode
        $body = (New-Object IO.StreamReader $response.GetResponseStream()).ReadToEnd()
        $location = $response.Headers['Location']
        $response.Close()
        if ($code -ge 300 -and $code -lt 400 -and $location -like '*Install.aspx*') { $path = ([Uri]::new([Uri]'http://x/', $location)).PathAndQuery; continue }
        break
    }
    Set-Content "$statusDir\install-output.html" $body
    if ($code -ne 200 -or $body -notmatch 'Successfully Installed Site|Installation Complete') { throw "DNN's install didn't complete (HTTP $code) - see install-output.html" }
    # DNN deletes the template itself once it has installed.
    Remove-Item "$site\Install\DotNetNuke.install.config", "$site\Install\DotNetNuke.install.config.resources" -Force -ErrorAction SilentlyContinue
    Sql $db "UPDATE dbo.Users SET UpdatePassword = 0 WHERE IsSuperUser = 1" | Out-Null
    $version = Sql $db "SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.Version ORDER BY VersionId DESC"
    Status "DNN $version installed"
    Remove-Item C:\ssei.exe, C:\setup, C:\dnn.zip -Recurse -Force -ErrorAction SilentlyContinue
    Status 'READY'
}
catch {
    Status "FAILED: $($_.Exception.Message)"
}
