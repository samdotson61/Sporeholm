<#
.SYNOPSIS
    Wrap the published macOS launcher binaries into a double-clickable SporeholmLauncher.app
    bundle and zip it — INTERMEDIATE artifact only; finalize on a Mac before shipping.

    A bare Mach-O binary does NOT open from Finder on a double-click — macOS GUI apps must be
    .app bundles. This builds a *universal* bundle: a tiny arch trampoline picks the arm64 or
    x64 binary, so the same .app runs natively on Apple Silicon and Intel. It also bundles the
    native dylibs (libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative) that `dotnet publish`
    leaves BESIDE the single file on macOS — single-file never embeds them there, and the GUI
    cannot render without them (v0.8.9 shipped without, and crashed on every Mac).

.NOTES
    The zip this writes is NOT runnable as-is (two things only a Mac can fix):
      • .NET's ZipArchive stamps the entries as MS-DOS-made, so extractors IGNORE the Unix
        exec bits we set below → the extracted .app can't spawn.
      • The binaries are unsigned → Apple Silicon kills them even with exec bits.
    Run launcher/scripts/package-macos.sh on a Mac to produce the shippable zip (it also
    re-signs + smoke-launches). This script exists so the Windows box can preview the layout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $X64,     # dist/osx-x64/SporeholmLauncher
    [Parameter(Mandatory)][string] $Arm64,   # dist/osx-arm64/SporeholmLauncher
    [Parameter(Mandatory)][string] $Icns,    # icon.icns
    [Parameter(Mandatory)][string] $OutZip,  # output: SporeholmLauncher-macos.zip
    [string] $Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'

$work  = Join-Path ([IO.Path]::GetTempPath()) ("macapp-" + [guid]::NewGuid().ToString('N').Substring(0,8))
$app   = Join-Path $work  'SporeholmLauncher.app'
$macos = Join-Path $app   'Contents/MacOS'
$res   = Join-Path $app   'Contents/Resources'
New-Item -ItemType Directory -Force -Path $macos, $res | Out-Null

Copy-Item $X64   (Join-Path $macos 'SporeholmLauncher-x64')
Copy-Item $Arm64 (Join-Path $macos 'SporeholmLauncher-arm64')
Copy-Item $Icns  (Join-Path $res   'icon.icns')

# Native dylibs: dotnet leaves these beside the single-file binary on macOS (never embeds
# them). They are universal (fat) — one copy serves both arch binaries. Without them the
# Avalonia GUI crashes at render init, so their absence is a hard error.
$dylibs = @(Get-ChildItem -Path (Split-Path $Arm64) -Filter 'lib*.dylib')
if ($dylibs.Count -lt 3) {
    throw "Expected >=3 native dylibs next to $Arm64 (libSkiaSharp/libHarfBuzzSharp/libAvaloniaNative), found $($dylibs.Count) - the .app would not render."
}
$dylibs | ForEach-Object { Copy-Item $_.FullName (Join-Path $macos $_.Name) }

# arch trampoline (LF endings + shebang) — this is CFBundleExecutable
$lf = "`n"
$tramp = "#!/bin/sh$lf" +
         "DIR=`"`$(cd `"`$(dirname `"`$0`")`" && pwd)`"$lf" +
         "if [ `"`$(uname -m)`" = `"arm64`" ]; then$lf" +
         "  exec `"`$DIR/SporeholmLauncher-arm64`" `"`$@`"$lf" +
         "else$lf" +
         "  exec `"`$DIR/SporeholmLauncher-x64`" `"`$@`"$lf" +
         "fi$lf"
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $macos 'SporeholmLauncher'), $tramp, $utf8)

# Info.plist
$plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Sporeholm Launcher</string>
  <key>CFBundleDisplayName</key><string>Sporeholm Launcher</string>
  <key>CFBundleIdentifier</key><string>com.samdotson.sporeholm.launcher</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>CFBundleExecutable</key><string>SporeholmLauncher</string>
  <key>CFBundleIconFile</key><string>icon.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
[IO.File]::WriteAllText((Join-Path $app 'Contents/Info.plist'), $plist, $utf8)

# zip the bundle, stamping Unix permissions into each entry's external attributes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $OutZip) { [IO.File]::Delete($OutZip) }
function ExtAttr([string]$octal){ $m = ([Convert]::ToInt32($octal,8) -bor 0x8000); [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($m * 65536)),0) }
$EXEC = ExtAttr '755'
$REG  = ExtAttr '644'
$zip = [IO.Compression.ZipFile]::Open($OutZip, 'Create')
function Add-Entry($full,$rel,$mode){
    $e = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
    $e.ExternalAttributes = $mode
    $in = [IO.File]::OpenRead($full); $out = $e.Open(); $in.CopyTo($out); $out.Dispose(); $in.Dispose()
}
$root = 'SporeholmLauncher.app/Contents'
Add-Entry (Join-Path $macos 'SporeholmLauncher')        "$root/MacOS/SporeholmLauncher"        $EXEC
Add-Entry (Join-Path $macos 'SporeholmLauncher-x64')    "$root/MacOS/SporeholmLauncher-x64"    $EXEC
Add-Entry (Join-Path $macos 'SporeholmLauncher-arm64')  "$root/MacOS/SporeholmLauncher-arm64"  $EXEC
$dylibs | ForEach-Object { Add-Entry (Join-Path $macos $_.Name) "$root/MacOS/$($_.Name)" $REG }
Add-Entry (Join-Path $app   'Contents/Info.plist')      "$root/Info.plist"                     $REG
Add-Entry (Join-Path $res   'icon.icns')                "$root/Resources/icon.icns"            $REG
$zip.Dispose()
[IO.Directory]::Delete($work, $true)
Write-Host "wrote $OutZip ($([math]::Round((Get-Item $OutZip).Length/1MB,1)) MB)"
Write-Host "NOTE: intermediate artifact - not runnable until finalized (signed + re-zipped)"
Write-Host "      on a Mac via launcher/scripts/package-macos.sh (see RELEASING.md)."
