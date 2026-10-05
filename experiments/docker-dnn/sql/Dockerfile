# escape=`
# SQL Server 2022 Express in a Windows container - the database half of the experiment (see README.md).
#
# Microsoft publishes SQL Server container images for Linux only. On a Windows-containers engine (a Windows Server host,
# or Docker Desktop switched to Windows containers) a Linux image can't run next to the IIS container, so this
# experiment installs SQL Server Express into Windows Server Core itself - as Microsoft's retired
# microsoft/mssql-server-windows-express image did. The databases live in C:\data (a volume) and are attached again
# whenever the container starts (scripts\sql-entrypoint.ps1).
FROM mcr.microsoft.com/windows/servercore:ltsc2022

SHELL ["powershell", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# The Express installer downloads the setup media (Core: the database engine only), which unpacks and installs
# silently. sysadmin: the container's administrators - the entrypoint sets the sa password from SA_PASSWORD.
RUN Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?linkid=2216019' -OutFile C:\ssei.exe -UseBasicParsing; `
    $p = Start-Process C:\ssei.exe -ArgumentList '/ACTION=Download', '/MEDIAPATH=C:\setup', '/MEDIATYPE=Core', '/QUIET' -Wait -PassThru; `
    if ($p.ExitCode -ne 0) { throw \"The SQL Server Express download failed ($($p.ExitCode)).\" }; `
    $media = Get-ChildItem C:\setup -Filter *.exe | Select-Object -First 1; `
    $p = Start-Process $media.FullName -ArgumentList '/q', '/x:C:\setup\sql' -Wait -PassThru; `
    if ($p.ExitCode -ne 0) { throw \"Unpacking $($media.Name) failed ($($p.ExitCode)).\" }; `
    $p = Start-Process C:\setup\sql\setup.exe -Wait -PassThru -NoNewWindow -ArgumentList '/q', '/ACTION=Install', `
        '/FEATURES=SQLEngine', '/INSTANCENAME=SQLEXPRESS', '/UPDATEENABLED=0', '/SECURITYMODE=SQL', `
        '/SAPWD=\"Bootstrap-0nly-Password\"', '/SQLSVCACCOUNT=\"NT AUTHORITY\System\"', `
        '/SQLSYSADMINACCOUNTS=\"BUILTIN\ADMINISTRATORS\"', '/TCPENABLED=1', '/NPENABLED=0', '/IACCEPTSQLSERVERLICENSETERMS'; `
    if ($p.ExitCode -ne 0) { `
        Get-ChildItem 'C:\Program Files\Microsoft SQL Server\*\Setup Bootstrap\Log\Summary.txt' | Get-Content | Write-Host; `
        throw \"SQL Server setup failed ($($p.ExitCode)).\" }; `
    Remove-Item C:\ssei.exe, C:\setup -Recurse -Force

# A fixed port (1433) instead of SQLEXPRESS's dynamic one, so "Server=sql" reaches it without SQL Browser.
RUN $tcp = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server' | `
        Where-Object { $_.PSChildName -like 'MSSQL*.SQLEXPRESS' } | Select-Object -First 1; `
    $ipAll = Join-Path $tcp.PSPath 'MSSQLServer\SuperSocketNetLib\Tcp\IPAll'; `
    Set-ItemProperty $ipAll -Name TcpDynamicPorts -Value ''; `
    Set-ItemProperty $ipAll -Name TcpPort -Value '1433'; `
    New-Item -ItemType Directory C:\data | Out-Null

COPY scripts/sql-entrypoint.ps1 scripts/sql.ps1 C:/scripts/

EXPOSE 1433
ENTRYPOINT ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\scripts\\sql-entrypoint.ps1"]
