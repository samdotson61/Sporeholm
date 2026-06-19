<#
.SYNOPSIS
    Publish the launcher GUI as a self-contained single-file binary for every
    desktop OS (no .NET install needed on the player's machine).

.EXAMPLE
    ./build-launcher.ps1                         # all RIDs → ../dist/<rid>/
    ./build-launcher.ps1 -Rids win-x64,linux-x64 # a subset
#>
[CmdletBinding()]
param(
    [string]   $OutDir = (Join-Path $PSScriptRoot '..\dist'),
    [string[]] $Rids   = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot '..\src\SporeholmLauncher.App\SporeholmLauncher.App.csproj'

foreach ($rid in $Rids) {
    $dest = Join-Path $OutDir $rid
    Write-Host "Publishing $rid → $dest"
    dotnet publish $proj -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $dest --nologo -v quiet
}

Write-Host "`nLauncher binaries:"
Get-ChildItem -Recurse $OutDir -Include 'SporeholmLauncher', 'SporeholmLauncher.exe' |
    ForEach-Object { Write-Host ("   {0,6:N0} MB   {1}" -f ($_.Length / 1MB), $_.FullName) }
