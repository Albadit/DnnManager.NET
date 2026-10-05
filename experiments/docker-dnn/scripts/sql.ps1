# Runs SQL from Windows PowerShell with .NET Framework's System.Data.SqlClient - in both containers, so neither needs
# sqlcmd. Dot-source it: . C:\scripts\sql.ps1

function Invoke-Sql {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$Query,
        [hashtable]$Parameters = @{},
        [int]$Timeout = 120
    )
    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = $Timeout
        foreach ($name in $Parameters.Keys) { [void]$command.Parameters.AddWithValue($name, $Parameters[$name]) }
        return $command.ExecuteScalar()
    }
    finally { $connection.Dispose() }
}

# Waits until a connection opens - SQL Server takes a while on a first start. Throws after $Seconds.
function Wait-Sql {
    param([Parameter(Mandatory)][string]$ConnectionString, [int]$Seconds = 300)
    $until = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try { [void](Invoke-Sql $ConnectionString 'SELECT 1' -Timeout 10); return }
        catch {
            if ((Get-Date) -gt $until) { throw "SQL Server didn't answer within $Seconds seconds: $($_.Exception.Message)" }
            Start-Sleep -Seconds 3
        }
    }
}
