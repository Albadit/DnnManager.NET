# After DNN's unattended install, as DnnManager.NET does (DnnInstaller.CompleteAsync, CleanUp): the template with the
# host password goes, and the host isn't made to change its password at the first sign-in. Prints the DNN version and
# host count the database reports.
param([Parameter(Mandatory)][string]$HostUser)
$ErrorActionPreference = 'Stop'
# The message on one line, last: the test keeps only the last lines of the output.
trap { Write-Output "error=$($_.Exception.Message)"; exit 1 }
. C:\scripts\sql.ps1

foreach ($file in 'DotNetNuke.install.config', 'DotNetNuke.install.config.resources') {
    Remove-Item "C:\site\Install\$file" -Force -ErrorAction SilentlyContinue
}
$db = "Data Source=$env:DNN_DB_SERVER;Initial Catalog=$env:DNN_DB_NAME;User ID=$env:DNN_DB_USER;Password=$env:DNN_DB_PASSWORD;"
$hosts = Invoke-Sql $db 'UPDATE dbo.Users SET UpdatePassword = 0 WHERE IsSuperUser = 1 AND Username = @u; SELECT @@ROWCOUNT;' @{ '@u' = $HostUser }
$version = Invoke-Sql $db "SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.Version ORDER BY VersionId DESC"
Write-Output "version=$version"
Write-Output "hosts=$hosts"
