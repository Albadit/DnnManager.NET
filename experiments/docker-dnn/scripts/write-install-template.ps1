# Writes DNN's install template (C:\site\Install\DotNetNuke.install.config) from the one DNN ships - the host account,
# the website and its alias - as DnnManager.NET does (src\DnnManager.Infrastructure\Dnn\DnnInstallTemplate.cs).
# Run it in the container right before Install.aspx?mode=install, and delete the file after: it holds the password.
param(
    [Parameter(Mandatory)][string]$Alias,
    [Parameter(Mandatory)][string]$HostUser,
    [Parameter(Mandatory)][string]$HostPassword,
    [string]$HostEmail = 'host@dnndev.me',
    [string]$WebsiteName = 'My Website'
)
$ErrorActionPreference = 'Stop'

$install = 'C:\site\Install'
[xml]$doc = Get-Content "$install\DotNetNuke.install.config.resources" -Raw
$root = $doc.dotnetnuke

function Set-Child($parent, [string]$name, [string]$value) {
    $node = $parent.SelectSingleNode($name)
    if (-not $node) { $node = $parent.AppendChild($doc.CreateElement($name)) }
    $node.InnerText = $value
}
function Get-Child($parent, [string]$name) {
    $node = $parent.SelectSingleNode($name)
    if (-not $node) { $node = $parent.AppendChild($doc.CreateElement($name)) }
    return $node
}

Set-Child $root 'installCulture' 'en-US'
$superuser = Get-Child $root 'superuser'
Set-Child $superuser 'username' $HostUser
Set-Child $superuser 'password' $HostPassword
Set-Child $superuser 'email' $HostEmail
Set-Child $superuser 'locale' 'en-US'
Set-Child $superuser 'updatepassword' 'false'
Set-Child (Get-Child $root 'settings') 'HostEmail' $HostEmail
# Without it DNN keeps web.config's connection string as the entrypoint wrote it.
foreach ($connection in @($root.SelectNodes('connection'))) { [void]$root.RemoveChild($connection) }

$portal = (Get-Child $root 'portals').SelectSingleNode('portal')
Set-Child $portal 'portalname' $WebsiteName
Set-Child (Get-Child $portal 'administrator') 'password' ([Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]]))
Set-Child $portal 'templatefile' 'Default Website.template'
$aliases = Get-Child $portal 'portalaliases'
$aliases.RemoveAll()
Set-Child $aliases 'portalalias' $Alias
Set-Child $portal 'ischild' 'false'

$doc.Save("$install\DotNetNuke.install.config")
Write-Host "Wrote $install\DotNetNuke.install.config for $Alias."
