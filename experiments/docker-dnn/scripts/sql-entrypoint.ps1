# The SQL Server Express container's start: the engine, the sa password from SA_PASSWORD, and every database in C:\data
# (the volume) attached again - so a new container (docker compose down / up) finds the databases the last one had.
$ErrorActionPreference = 'Stop'
. C:\scripts\sql.ps1

if (-not $env:SA_PASSWORD) { throw 'Set SA_PASSWORD.' }

Start-Service 'MSSQL$SQLEXPRESS'
# As the container's administrator - a sysadmin since setup (SQLSYSADMINACCOUNTS).
$local = 'Server=localhost\SQLEXPRESS;Database=master;Integrated Security=True;'
Wait-Sql $local

# DDL takes no parameters: the password goes in quoted by QUOTENAME.
Invoke-Sql $local ("DECLARE @sql nvarchar(max) = N'ALTER LOGIN sa WITH PASSWORD = ' + QUOTENAME(@p, '''') + " +
    "N', CHECK_POLICY = OFF; ALTER LOGIN sa ENABLE;'; EXEC (@sql);") @{ '@p' = $env:SA_PASSWORD } | Out-Null

foreach ($mdf in Get-ChildItem C:\data -Filter *.mdf) {
    $name = $mdf.BaseName
    if (Invoke-Sql $local 'SELECT DB_ID(@n)' @{ '@n' = $name }) { continue }
    $ldf = Join-Path C:\data "$($name)_log.ldf"
    $files = "(FILENAME = N'$($mdf.FullName)')" + $(if (Test-Path $ldf) { ", (FILENAME = N'$ldf')" } else { '' })
    Invoke-Sql $local "CREATE DATABASE [$name] ON $files FOR ATTACH" | Out-Null
    Write-Host "Attached $name from $($mdf.FullName)."
}

Write-Host 'SQL Server Express is ready on port 1433.'
# The container lives as long as the engine does.
while ((Get-Service 'MSSQL$SQLEXPRESS').Status -eq 'Running') { Start-Sleep -Seconds 10 }
throw 'SQL Server stopped.'
