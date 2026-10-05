# Runs one query against this container's DNN database (from its DNN_DB_* settings) and prints the first value - for the
# tests on the host: docker compose exec -T web powershell -File C:\scripts\query.ps1 -Query "SELECT ..."
param([Parameter(Mandatory)][string]$Query)
$ErrorActionPreference = 'Stop'
. C:\scripts\sql.ps1
Invoke-Sql "Data Source=$env:DNN_DB_SERVER;Initial Catalog=$env:DNN_DB_NAME;User ID=$env:DNN_DB_USER;Password=$env:DNN_DB_PASSWORD;" $Query
