<#
.SYNOPSIS
    Package a Sporeholm release the launcher can install: zip the exported game
    per OS, compute checksums, write manifest.json, and (optionally) publish it
    all to GitHub Releases.

.DESCRIPTION
    The launcher fetches a manifest.json that points at one zip per OS. This
    script produces that manifest + zips from already-exported Godot builds, and
    can upload them to the repo's GitHub Releases via the `gh` CLI.

    Exporting the game itself is a Godot step (Project > Export). Today only a
    "Windows Desktop" export preset exists; add "Linux/X11" and "macOS" presets
    in Godot to ship those platforms. Point -WindowsBuild/-LinuxBuild/-MacBuild
    at each exported folder.

.EXAMPLE
    # Windows-only release, written to .\release, published to GitHub:
    ./package-release.ps1 -WindowsBuild C:\exports\sporeholm-win -Publish

.EXAMPLE
    # All three platforms, explicit version + notes, local only (no publish):
    ./package-release.ps1 -Version v0.8.9 `
        -WindowsBuild .\exports\win -LinuxBuild .\exports\linux -MacBuild .\exports\mac
#>
[CmdletBinding()]
param(
    [string] $Version,                                   # default: read from the game's project.godot
    [string] $GameRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,  # launcher/ lives inside the game repo
    [string] $WindowsBuild,
    [string] $LinuxBuild,
    [string] $MacBuild,
    [string] $OutDir = (Join-Path $PSScriptRoot '..\release'),
    [string] $LauncherDist = (Join-Path $PSScriptRoot '..\dist'),   # build-launcher.ps1 + make-mac-app.ps1 output
    [string] $Channel = 'stable',
    [string] $Notes,
    [switch] $Publish,
    [string] $RepoSlug = 'samdotson61/Sporeholm'
)

$ErrorActionPreference = 'Stop'

function Resolve-Version {
    if ($Version) { return $Version }
    $proj = Join-Path $GameRepo 'project.godot'
    if (-not (Test-Path $proj)) { throw "No -Version given and project.godot not found at $proj" }
    $line = Select-String -Path $proj -Pattern 'config/version="([^"]+)"' | Select-Object -First 1
    if (-not $line) { throw "Could not read config/version from $proj — pass -Version explicitly." }
    return $line.Matches[0].Groups[1].Value
}

function Resolve-Notes {
    if ($Notes) { return $Notes }
    $cl = Join-Path $GameRepo 'changelog.md'
    if (-not (Test-Path $cl)) { return "" }
    # Take the first "## [x.y.z] ..." section as the what's-new excerpt.
    $lines = Get-Content $cl
    $start = ($lines | Select-String -Pattern '^##\s' | Select-Object -First 1)
    if (-not $start) { return "" }
    $i = $start.LineNumber           # 1-based, first heading
    $excerpt = @($lines[$i - 1])
    for ($j = $i; $j -lt $lines.Count; $j++) {
        if ($lines[$j] -match '^##\s') { break }         # next section → stop
        $excerpt += $lines[$j]
    }
    return ($excerpt -join "`n").Trim()
}

# Build the manifest's "launcher" section: each OS's launcher binary (so the launcher can
# update ITSELF), keyed by the asset name it's uploaded under. Returns $null if none are built.
function Resolve-LauncherManifest {
    $map = [ordered]@{
        windows = @{ path = (Join-Path $LauncherDist 'win-x64\SporeholmLauncher.exe'); name = 'SporeholmLauncher.exe' }
        linux   = @{ path = (Join-Path $LauncherDist 'linux-x64\SporeholmLauncher');   name = 'SporeholmLauncher-linux' }
        macos   = @{ path = (Join-Path $LauncherDist 'SporeholmLauncher-macos.zip');   name = 'SporeholmLauncher-macos.zip' }
    }
    $lfiles = @{}
    foreach ($os in $map.Keys) {
        $p = $map[$os].path
        if (Test-Path $p) {
            $lfiles[$os] = [ordered]@{
                name   = $map[$os].name
                sha256 = (Get-FileHash -Algorithm SHA256 -Path $p).Hash.ToLower()
                size   = (Get-Item $p).Length
            }
        }
    }
    if ($lfiles.Count -eq 0) { return $null }

    $lver = '0.0.0'
    $info = Join-Path $GameRepo 'launcher\src\SporeholmLauncher.Core\LauncherInfo.cs'
    if (Test-Path $info) {
        $m = Select-String -Path $info -Pattern 'Version\s*=\s*"([^"]+)"' | Select-Object -First 1
        if ($m) { $lver = $m.Matches[0].Groups[1].Value }
    }
    return [ordered]@{ version = $lver; files = $lfiles }
}

# Drop a version.txt at the root of the zip (without touching the caller's export
# folder) so the *installed* build declares its own version — the launcher reads it
# back off disk for update parity even if it has no install record of its own.
function Add-VersionStamp([string]$zipPath, [string]$ver) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Update')
    try {
        $stale = $zip.GetEntry('version.txt'); if ($stale) { $stale.Delete() }
        $entry = $zip.CreateEntry('version.txt')
        $sw = New-Object System.IO.StreamWriter($entry.Open())
        try { $sw.Write($ver) } finally { $sw.Dispose() }
    } finally { $zip.Dispose() }
}

function Add-Build([hashtable]$files, [string]$os, [string]$buildDir, [string]$ver) {
    if (-not $buildDir) { return }
    if (-not (Test-Path $buildDir)) { throw "Build folder for '$os' not found: $buildDir" }
    if (-not (Get-ChildItem -Path $buildDir -File -Recurse)) { throw "Build folder for '$os' is empty: $buildDir" }
    $zipName = "Sporeholm-$os.zip"
    $zipPath = Join-Path $OutDir $zipName
    Write-Host "  zipping $os → $zipName"
    Compress-Archive -Path (Join-Path $buildDir '*') -DestinationPath $zipPath -Force
    Add-VersionStamp $zipPath $ver
    $sha  = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLower()
    $size = (Get-Item $zipPath).Length
    $files[$os] = [ordered]@{ name = $zipName; sha256 = $sha; size = $size }
}

# --- main ---------------------------------------------------------------------

$ver = Resolve-Version
$notes = Resolve-Notes
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "Packaging Sporeholm $ver ($Channel)"
$files = @{}   # plain hashtable so it passes to Add-Build by reference (an [ordered] dict would be copied)
Add-Build $files 'windows' $WindowsBuild $ver
Add-Build $files 'linux'   $LinuxBuild   $ver
Add-Build $files 'macos'   $MacBuild     $ver
if ($files.Count -eq 0) { throw "Provide at least one of -WindowsBuild / -LinuxBuild / -MacBuild." }

$manifest = [ordered]@{
    version     = $ver
    channel     = $Channel
    notes       = $notes
    releasedUtc = (Get-Date).ToUniversalTime().ToString('o')
    files       = $files
}
$launcherManifest = Resolve-LauncherManifest
if ($launcherManifest) {
    $manifest['launcher'] = $launcherManifest
    Write-Host "  launcher manifest: v$($launcherManifest.version) ($($launcherManifest.files.Count) OS) — enables launcher self-update"
}
$manifestName = if ($Channel -eq 'stable') { 'manifest.json' } else { "manifest-$Channel.json" }
$manifestPath = Join-Path $OutDir $manifestName
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "  wrote $manifestName"

# Publish the changelog alongside the manifest so the launcher's news feed has a source.
$changelogSrc = Join-Path $GameRepo 'changelog.md'
$changelogOut = Join-Path $OutDir 'changelog.md'
if (Test-Path $changelogSrc) { Copy-Item $changelogSrc $changelogOut -Force; Write-Host "  copied changelog.md" }

Write-Host "`nRelease ready in $OutDir :"
Get-ChildItem $OutDir | ForEach-Object { Write-Host ("   {0,12}  {1}" -f $_.Length, $_.Name) }

if ($Publish) {
    $assets = @($manifestPath) + ($files.Values | ForEach-Object { Join-Path $OutDir $_.name })
    if (Test-Path $changelogOut) { $assets += $changelogOut }
    Write-Host "`nPublishing GitHub release $ver to $RepoSlug …"
    # Create the release if it doesn't exist; otherwise just (re)upload the assets.
    $exists = (gh release view $ver --repo $RepoSlug 2>$null)
    if (-not $exists) {
        gh release create $ver --repo $RepoSlug --title $ver --notes $notes @assets
    } else {
        gh release upload $ver --repo $RepoSlug --clobber @assets
    }
    Write-Host "Published. The launcher (GitHub source) will pick it up on next check."
}
