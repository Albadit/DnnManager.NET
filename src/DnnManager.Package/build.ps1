<#
.SYNOPSIS
    Builds DNN Manager as an MSIX package: publish\DnnManager_<version>_x64.msix

.DESCRIPTION
    1. Publishes the app self-contained for win-x64 as a single file into src\DnnManager.Package\bin\layout, as the
       installer does.
    2. Draws the package's logos from the app icon and writes AppxManifest.xml with the version and the publisher.
    3. Packs it with the Windows SDK's makeappx.exe and signs it with signtool.exe (SHA-256, with an RFC 3161 time stamp
       so the signature outlives the certificate).

    The package installs outside the Microsoft Store. DNN Manager in it asks for Administrator rights when it starts,
    as it does from Setup (allowElevation). -Broker builds the experiment instead: DNN Manager without Administrator
    rights and the DNN Manager Broker service with it (.docs/privileged-broker.md).

    A package must be signed to be installed, by a certificate whose subject is the manifest's Publisher - which is
    taken from the certificate. For users that is a certificate Windows trusts (-CertificateThumbprint). For a test,
    -TestCertificate makes a self-signed one (in CurrentUser\My) and exports its public part to
    src\DnnManager.Package\bin\DnnManager-test.cer; the PC that installs the package must trust it - see
    .docs/releasing.md, The MSIX package.

.EXAMPLE
    .\src\DnnManager.Package\build.ps1 -TestCertificate
.EXAMPLE
    .\src\DnnManager.Package\build.ps1 -Version 1.8.0 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
.EXAMPLE
    .\src\DnnManager.Package\build.ps1 -Broker -TestCertificate
#>
[CmdletBinding()]
param(
    # The version to build (1.7.9, or 1.7.9-rc.1) instead of the newest version tag. The package's own version is four
    # numbers without the suffix: 1.7.9.0.
    [string]$Version,
    # The certificate to sign with (in CurrentUser\My or LocalMachine\My, a hardware token's or a cloud HSM's included).
    [string]$CertificateThumbprint,
    # Makes (or reuses) a self-signed certificate CN=DnnManager Test and signs with it - for testing only.
    [switch]$TestCertificate,
    # The experiment: DNN Manager without Administrator rights, and the DNN Manager Broker service in the package.
    [switch]$Broker,
    # The RFC 3161 time-stamp server for a signature with -CertificateThumbprint.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    # Reuse bin\layout from an earlier run.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $root 'DnnManager.csproj'
$icon = Join-Path $root 'src\DnnManager.Presentation\Assets\dnn.ico'
$binDir = Join-Path $PSScriptRoot 'bin'
$layout = Join-Path $binDir 'layout'
$outputDir = Join-Path $root 'publish'
$testSubject = 'CN=DnnManager Test'

function Get-AppVersion {
    $v = & dotnet msbuild $project -nologo -t:VersionFromGitTag -getProperty:Version
    if ($LASTEXITCODE -ne 0 -or -not $v) { throw "Can't read the version of $project (exit code $LASTEXITCODE)." }
    return "$v".Trim()
}

# The newest Windows SDK's copy of a tool (makeappx.exe, signtool.exe).
function Find-SdkTool([string]$name) {
    $tool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\$name") -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found - install the Windows SDK (it comes with Visual Studio's desktop workloads)." }
    return $tool.FullName
}

function Invoke-Publish([string]$appVersion, [string]$fileVersion) {
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
    # As the installer publishes it: single file, uncompressed (the package compresses), native DLLs beside the exe.
    & dotnet publish $project -nologo -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
        "-p:Version=$appVersion" "-p:AssemblyVersion=$fileVersion" "-p:FileVersion=$fileVersion" `
        -o $layout
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
}

# The logos the manifest names: the icon's largest frame, scaled onto a transparent square.
function New-Logos {
    Add-Type -AssemblyName System.Drawing
    $assets = Join-Path $layout 'Assets'
    New-Item -ItemType Directory -Force $assets | Out-Null
    $bytes = [IO.File]::ReadAllBytes($icon)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = 0..($count - 1) | Sort-Object { $w = $bytes[6 + $_ * 16]; if ($w -eq 0) { 256 } else { $w } } -Descending | Select-Object -First 1
    $size = [BitConverter]::ToInt32($bytes, 6 + $best * 16 + 8)
    $offset = [BitConverter]::ToInt32($bytes, 6 + $best * 16 + 12)
    $isPng = $bytes[$offset] -eq 0x89 -and $bytes[$offset + 1] -eq 0x50

    foreach ($logo in @(@{ Name = 'Square44x44Logo.png'; Size = 44 }, @{ Name = 'Square150x150Logo.png'; Size = 150 }, @{ Name = 'StoreLogo.png'; Size = 50 })) {
        $stream = $null
        $image = if ($isPng) {
            $stream = New-Object IO.MemoryStream (, [byte[]]$bytes[$offset..($offset + $size - 1)])
            [System.Drawing.Image]::FromStream($stream)
        }
        else { (New-Object System.Drawing.Icon $icon, 256, 256).ToBitmap() }
        $bitmap = New-Object System.Drawing.Bitmap $logo.Size, $logo.Size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($image, 0, 0, $logo.Size, $logo.Size)
            $bitmap.Save((Join-Path $assets $logo.Name), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose(); $bitmap.Dispose(); $image.Dispose()
            if ($stream) { $stream.Dispose() }
        }
    }
}

function Find-Certificate([string]$thumbprint) {
    $clean = $thumbprint -replace '\s', ''
    $cert = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') | ForEach-Object { Get-ChildItem $_ } |
        Where-Object { $_.Thumbprint -eq $clean } | Select-Object -First 1
    if (-not $cert) { throw "No certificate with the thumbprint $clean in CurrentUser\My or LocalMachine\My." }
    if (-not $cert.HasPrivateKey) { throw "The certificate $clean ($($cert.Subject)) has no private key here to sign with." }
    if ($cert.NotAfter -lt (Get-Date)) { throw "The certificate $clean ($($cert.Subject)) expired on $($cert.NotAfter)." }
    return $cert
}

function Get-TestCertificate {
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $testSubject -and $_.NotAfter -gt (Get-Date).AddDays(1) -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    $cert = if ($existing) { $existing } else {
        Write-Host "Making a self-signed test certificate $testSubject (CurrentUser\My)..."
        New-SelfSignedCertificate -Type Custom -Subject $testSubject -KeyUsage DigitalSignature -FriendlyName 'DNN Manager MSIX test' `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddMonths(3) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $cer = Join-Path $binDir 'DnnManager-test.cer'
    New-Item -ItemType Directory -Force $binDir | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "Its public part (trust it to install the package): $cer"
    return $cert
}

# The name people see as the publisher: the certificate's O=, else its CN=.
function Get-DisplayName([string]$subject) {
    foreach ($field in 'O', 'CN') {
        if ($subject -match "(?:^|,\s*)$field=(`"[^`"]+`"|[^,]+)") { return $Matches[1].Trim('"') }
    }
    return $subject
}

if ($TestCertificate -and $CertificateThumbprint) { throw 'Give -CertificateThumbprint or -TestCertificate, not both.' }

$appVersion = if ($Version) { $Version.TrimStart('v') } else { Get-AppVersion }
if ($appVersion -notmatch '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') { throw "'$appVersion' isn't a version like 1.7.9 or 1.7.9-rc.1." }
$fileVersion = "$($Matches[1]).0"

# The certificate first: its subject is the package's publisher.
$cert = if ($TestCertificate) { Get-TestCertificate } elseif ($CertificateThumbprint) { Find-Certificate $CertificateThumbprint } else { $null }
$publisher = if ($cert) { $cert.Subject } else { $testSubject }
$kind = if ($Broker) { 'with the DNN Manager Broker (experiment)' } else { 'elevating as from Setup' }
Write-Host "DNN Manager $appVersion - package $fileVersion, $kind, publisher $publisher"

if (-not $SkipPublish) { Invoke-Publish $appVersion $fileVersion }
if (-not (Test-Path (Join-Path $layout 'DnnManager.exe'))) { throw "No DnnManager.exe in $layout - run without -SkipPublish." }

New-Logos
# Keep the one kind's blocks, drop the other's.
$keep, $drop = if ($Broker) { 'BROKER', 'ELEVATE' } else { 'ELEVATE', 'BROKER' }
$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml'))
$manifest = [regex]::Replace($manifest, "[ \t]*<!--$drop-BEGIN-->.*?<!--$drop-END-->\r?\n", '', 'Singleline')
$manifest = [regex]::Replace($manifest, "[ \t]*<!--$keep-(BEGIN|END)-->\r?\n", '')
$manifest = $manifest.Replace('$Publisher$', [Security.SecurityElement]::Escape($publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape((Get-DisplayName $publisher))).
    Replace('$Version$', $fileVersion)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force $outputDir | Out-Null
$msix = Join-Path $outputDir "DnnManager_$($appVersion)_x64.msix"
$packed = & (Find-SdkTool 'makeappx.exe') pack /o /h SHA256 /d $layout /p $msix 2>&1
if ($LASTEXITCODE -ne 0) {
    $packed | Where-Object { "$_" -match 'error' } | ForEach-Object { Write-Host "$_" -ForegroundColor Red }
    throw "makeappx failed (exit code $LASTEXITCODE)."
}

if ($cert) {
    $sign = @('sign', '/fd', 'SHA256', '/sha1', $cert.Thumbprint)
    # A time stamp keeps the signature valid after the certificate expires; a test certificate does without.
    if (-not $TestCertificate) { $sign += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    & (Find-SdkTool 'signtool.exe') @sign $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit code $LASTEXITCODE)." }
}
else {
    Write-Host 'Not signed (no -CertificateThumbprint or -TestCertificate): Windows won''t install it.' -ForegroundColor Yellow
}

Write-Host "Package: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB)"
