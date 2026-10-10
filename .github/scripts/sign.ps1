<#
.SYNOPSIS
    Signs files with Authenticode through Azure Artifact Signing - the release workflow's signing hook.

.DESCRIPTION
    Run by the release workflow (.github\workflows\release.yml) only when signing is set up (the repository variables
    in .docs\releasing.md, "Code signing"), after azure/login has signed the job in to Azure with OpenID Connect -
    there is no signing secret in the repository.

    -Setup, once per job: downloads the Artifact Signing client (the NuGet package Microsoft.ArtifactSigning.Client,
    pinned and checked against its SHA-512), writes its metadata.json from ARTIFACT_SIGNING_ENDPOINT,
    ARTIFACT_SIGNING_ACCOUNT and ARTIFACT_SIGNING_PROFILE, finds signtool.exe, and hands their paths to the next steps
    (GITHUB_ENV).

    -Path <file>: signs the file (SHA-256, timestamped by Microsoft's RFC 3161 server, so the signature outlives the
    short-lived certificate) and checks the signature. src\DnnManager.Installer\build.ps1 -SignScript calls it for
    DnnManager.exe, and Inno Setup for Setup and its uninstaller.

.EXAMPLE
    .github\scripts\sign.ps1 -Setup
.EXAMPLE
    .github\scripts\sign.ps1 -Path publish\DnnManager_Portable-1.9.0-x64.exe
#>
[CmdletBinding()]
param(
    # The files to sign.
    [string[]]$Path,
    # Prepares the signing client for the job's next steps.
    [switch]$Setup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The Artifact Signing client (the dlib signtool loads) and nuget.org's SHA-512 of its package (the catalog's
# packageHash) - change both together.
$clientVersion = '1.0.128'
$clientSha512 = 'mPBqaR9Pwvoi8Z3PhVZzPphgf775GjEsRTubB5jMkIja4KyzbjibVSoRtNIyAyR4W4VBwrUQkackwFvF31y/lQ=='
$timestampUrl = 'http://timestamp.acs.microsoft.com'

function Invoke-SignTool([string[]]$argv) {
    & $env:SIGN_SIGNTOOL @argv
    if ($LASTEXITCODE -ne 0) { throw "signtool $($argv[0]) failed (exit code $LASTEXITCODE)." }
}

if ($Setup) {
    foreach ($name in 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'RUNNER_TEMP', 'GITHUB_ENV') {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name isn't set - signing can't be set up." }
    }
    if ($env:ARTIFACT_SIGNING_ENDPOINT -notmatch '^https://[a-z0-9]+\.codesigning\.azure\.net/?$') {
        throw "ARTIFACT_SIGNING_ENDPOINT ($env:ARTIFACT_SIGNING_ENDPOINT) isn't an Artifact Signing endpoint (https://<region>.codesigning.azure.net)."
    }
    $dir = Join-Path $env:RUNNER_TEMP 'artifact-signing'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null

    # The client, checked before a byte of it is extracted.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $package = Join-Path $dir 'client.nupkg'
    Invoke-WebRequest -UseBasicParsing "https://www.nuget.org/api/v2/package/Microsoft.ArtifactSigning.Client/$clientVersion" -OutFile $package
    $sha = [Security.Cryptography.SHA512]::Create()
    try { $hash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($package))) } finally { $sha.Dispose() }
    if ($hash -ne $clientSha512) { throw "Microsoft.ArtifactSigning.Client $clientVersion from nuget.org isn't the package it should be (its SHA-512 differs)." }
    Expand-Archive $package -DestinationPath (Join-Path $dir 'client') -Force
    $dlib = Join-Path $dir 'client\bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dlib)) { throw "The Artifact Signing client has no bin\x64\Azure.CodeSigning.Dlib.dll." }

    # Signed in through the Azure CLI only (azure/login before this step) - no other credential is tried.
    $metadata = Join-Path $dir 'metadata.json'
    $settings = [ordered]@{
        Endpoint               = $env:ARTIFACT_SIGNING_ENDPOINT
        CodeSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT
        CertificateProfileName = $env:ARTIFACT_SIGNING_PROFILE
        ExcludeCredentials     = @('EnvironmentCredential', 'ManagedIdentityCredential', 'WorkloadIdentityCredential',
            'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzurePowerShellCredential',
            'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
    }
    [IO.File]::WriteAllText($metadata, ($settings | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

    # The newest x64 signtool.exe of the Windows SDK on the runner.
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signtool = Get-ChildItem $kits -Directory -Filter '10.*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $signtool) { throw "No x64 signtool.exe under $kits." }

    [IO.File]::AppendAllText($env:GITHUB_ENV, "SIGN_SIGNTOOL=$signtool`nSIGN_DLIB=$dlib`nSIGN_METADATA=$metadata`n", (New-Object Text.UTF8Encoding $false))
    Write-Host "Signing is set up: $signtool, Artifact Signing client $clientVersion, account $env:ARTIFACT_SIGNING_ACCOUNT, profile $env:ARTIFACT_SIGNING_PROFILE."
}

foreach ($file in @($Path | Where-Object { $_ })) {
    if (-not $env:SIGN_SIGNTOOL -or -not $env:SIGN_DLIB -or -not $env:SIGN_METADATA) { throw 'Signing isn''t set up - run sign.ps1 -Setup first.' }
    $full = (Resolve-Path -LiteralPath $file).Path
    Invoke-SignTool @('sign', '/v', '/fd', 'SHA256', '/tr', $timestampUrl, '/td', 'SHA256', '/dlib', $env:SIGN_DLIB, '/dmdf', $env:SIGN_METADATA, $full)
    Invoke-SignTool @('verify', '/pa', $full)
}
