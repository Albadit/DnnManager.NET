# The DNN container's start. The first time (an empty site volume): DNN's files go into C:\site, the app pool may
# change them, web.config points at the database, and the database is created. Every time: hands over to IIS through
# ServiceMonitor, as the aspnet base image does.
$ErrorActionPreference = 'Stop'
. C:\scripts\sql.ps1

foreach ($name in 'DNN_DB_SERVER', 'DNN_DB_NAME', 'DNN_DB_USER', 'DNN_DB_PASSWORD') {
    if (-not (Get-Item "env:$name" -ErrorAction SilentlyContinue)) { throw "Set $name." }
}
$site = 'C:\site'
$database = "Data Source=$env:DNN_DB_SERVER;Initial Catalog=$env:DNN_DB_NAME;User ID=$env:DNN_DB_USER;Password=$env:DNN_DB_PASSWORD;"

if (-not (Test-Path "$site\web.config")) {
    Write-Host "First start: DNN $env:DNN_VERSION goes into $site."
    # Before the copy, so every file inherits it: DNN writes web.config, Portals, App_Data and its install logs.
    icacls $site /grant 'IIS AppPool\DefaultAppPool:(OI)(CI)M' | Out-Null
    Copy-Item C:\dnn-package\* $site -Recurse -Force

    [xml]$config = Get-Content "$site\web.config" -Raw
    foreach ($add in $config.configuration.connectionStrings.add) { if ($add.name -eq 'SiteSqlServer') { $add.connectionString = $database } }
    foreach ($add in $config.configuration.appSettings.add) { if ($add.key -eq 'SiteSqlServer') { $add.value = $database } }
    $config.Save("$site\web.config")
}

# The database - in the SQL container's C:\data, its volume - unless it is there already.
$master = "Data Source=$env:DNN_DB_SERVER;Initial Catalog=master;User ID=$env:DNN_DB_USER;Password=$env:DNN_DB_PASSWORD;"
Wait-Sql $master
$name = $env:DNN_DB_NAME
if (-not (Invoke-Sql $master 'SELECT DB_ID(@n)' @{ '@n' = $name })) {
    Invoke-Sql $master ("CREATE DATABASE [$name] ON (NAME = N'$name', FILENAME = N'C:\data\$name.mdf') " +
        "LOG ON (NAME = N'$($name)_log', FILENAME = N'C:\data\$($name)_log.ldf')") | Out-Null
    Write-Host "Created the database $name."
}

Write-Host "DNN is served on port 80 from $site."
& C:\ServiceMonitor.exe w3svc
