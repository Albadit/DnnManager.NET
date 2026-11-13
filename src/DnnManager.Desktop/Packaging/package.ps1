<#
  Packages a published DNN Manager (src/DnnManager.Desktop) for its system - free to make, nothing paid for.

  -Kind executable (what a release carries - the Desktop workflow): the program itself, one file -
    DnnManager-<version>-<name> (DnnManager-2.0.0-macos-arm64, DnnManager-2.0.0-linux-x64), run-able. On macOS it is
    signed ad hoc (codesign -s -): Apple Silicon starts only signed code, and an ad-hoc signature needs no Apple account.
    A download loses the run permission and macOS marks it as downloaded: the first time, in Terminal,
    chmod +x <file> (and on a Mac: xattr -d com.apple.quarantine <file>) - see .docs/cross-platform.md.

  -Kind bundle (the VS Code task "Publish" -> Setup):
    macOS   DNN Manager.app in a zip, signed ad hoc. Not notarized - the first start is Right-click -> Open.
    Linux   a tar.gz: the program, run-able, with a .desktop file to add it to the menu.
    Windows a zip of the program.

  Runs where the system's tools are: macOS on a Mac (codesign, ditto), Linux on Linux or a Mac (chmod, tar). By hand:

    dotnet publish src/DnnManager.Desktop -c Release -r osx-arm64 -o out/app
    ./src/DnnManager.Desktop/Packaging/package.ps1 -Publish out/app -Rid osx-arm64 -Version 2.0.0 -Out out/dist -Name macos-arm64
#>
param(
    [Parameter(Mandatory)] [string] $Publish,
    [Parameter(Mandatory)] [string] $Rid,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Out,
    [Parameter(Mandatory)] [string] $Name,
    [ValidateSet('executable', 'bundle')] [string] $Kind = 'executable'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Publish = (Resolve-Path $Publish).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) "dnnmanager-package-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $stage | Out-Null
$exe = 'DnnManager.Desktop'
# macOS wants numbers only in the bundle's version (2.0.0); the file names keep the whole version (2.0.0-rc1).
$bundleVersion = $Version -replace '-.*$', ''
$base = "DnnManager-$Version-$Name"

# What a user needs: the program and the native libraries next to it - not the symbols or the XML docs.
function Copy-App([string] $to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    Get-ChildItem $Publish -File | Where-Object { $_.Extension -notin '.pdb', '.xml' } | Copy-Item -Destination $to
}

function Invoke-Tool([string] $tool, [string[]] $arguments) {
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "$tool failed (exit code $LASTEXITCODE)." }
}

try {
    if ($Kind -eq 'executable') {
        # One self-contained file (the publish bundles the native libraries into it): copied under the release's name.
        if ($Rid -like 'win-*') { throw 'A Windows release has Setup and the portable exe (release.yml) - -Kind executable is for macOS and Linux.' }
        $file = Join-Path $Out $base
        Copy-Item (Join-Path $Publish $exe) $file -Force
        Invoke-Tool chmod @('+x', $file)
        if ($Rid -like 'osx-*') {
            Invoke-Tool codesign @('--force', '--sign', '-', $file)
            Invoke-Tool codesign @('--verify', '--strict', $file)
        }
    }
    elseif ($Rid -like 'osx-*') {
        $app = Join-Path $stage 'DNN Manager.app'
        $macos = Join-Path $app 'Contents/MacOS'
        $resources = Join-Path $app 'Contents/Resources'
        Copy-App $macos
        New-Item -ItemType Directory -Force $resources | Out-Null
        Invoke-Tool chmod @('+x', (Join-Path $macos $exe))

        # The icon: the Windows app's dnn.ico, made an .icns with macOS's own tools - left out if they can't.
        $icon = ''
        $ico = Join-Path $PSScriptRoot '../../DnnManager.Presentation/Assets/dnn.ico'
        try {
            $iconset = Join-Path $stage 'dnn.iconset'
            New-Item -ItemType Directory $iconset | Out-Null
            $png = Join-Path $stage 'dnn.png'
            Invoke-Tool sips @('-s', 'format', 'png', $ico, '--out', $png)
            foreach ($size in 16, 32, 128, 256, 512) {
                Invoke-Tool sips @('-z', $size, $size, $png, '--out', (Join-Path $iconset "icon_${size}x${size}.png"))
                Invoke-Tool sips @('-z', ($size * 2), ($size * 2), $png, '--out', (Join-Path $iconset "icon_${size}x${size}@2x.png"))
            }
            Invoke-Tool iconutil @('-c', 'icns', $iconset, '-o', (Join-Path $resources 'dnn.icns'))
            $icon = "`n  <key>CFBundleIconFile</key><string>dnn.icns</string>"
        }
        catch { Write-Warning "No icon: $_" }

        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DNN Manager</string>
  <key>CFBundleDisplayName</key><string>DNN Manager</string>
  <key>CFBundleIdentifier</key><string>com.albadit.dnnmanager</string>
  <key>CFBundleVersion</key><string>$bundleVersion</string>
  <key>CFBundleShortVersionString</key><string>$bundleVersion</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$exe</string>$icon
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
        [IO.File]::WriteAllText((Join-Path $app 'Contents/Info.plist'), $plist, [Text.UTF8Encoding]::new($false))

        # Ad hoc: no certificate, no Apple account - what Apple Silicon needs to start it.
        Invoke-Tool codesign @('--force', '--deep', '--sign', '-', $app)
        Invoke-Tool codesign @('--verify', '--deep', '--strict', $app)
        # ditto keeps what macOS needs (the signature, permissions) in the zip.
        Invoke-Tool ditto @('-c', '-k', '--sequesterRsrc', '--keepParent', $app, (Join-Path $Out "$base.zip"))
    }
    elseif ($Rid -like 'linux-*') {
        $folder = Join-Path $stage $base
        Copy-App $folder
        Invoke-Tool chmod @('+x', (Join-Path $folder $exe))
        $desktop = @"
[Desktop Entry]
Type=Application
Name=DNN Manager
Comment=Compare DNN projects with their servers, push and pull their files
Exec=sh -c '"`$(dirname "%k")/$exe"'
Terminal=false
Categories=Development;
"@
        [IO.File]::WriteAllText((Join-Path $folder 'dnn-manager.desktop'), $desktop, [Text.UTF8Encoding]::new($false))
        Invoke-Tool tar @('-czf', (Join-Path $Out "$base.tar.gz"), '-C', $stage, $base)
    }
    else {
        $folder = Join-Path $stage $base
        Copy-App $folder
        Compress-Archive -Path $folder -DestinationPath (Join-Path $Out "$base.zip") -Force
    }
    Get-ChildItem $Out | ForEach-Object { "Packaged $($_.Name) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
