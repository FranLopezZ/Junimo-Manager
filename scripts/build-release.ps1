# Builds distributable packages for Windows and Linux/Steam Deck into ./release
# Usage: powershell -File scripts/build-release.ps1 [-Version 0.5.3]
param([string]$Version = "0.5.2", [switch]$Nexus)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
# -Nexus builds the edition hosted on Nexus Mods (no downloads from GitHub) into release/nexus, so the regular packages are untouched.
$out = Join-Path $root "release"
if ($Nexus) { $out = Join-Path $out "nexus" }
$tag = if ($Nexus) { "-nexus" } else { "" }
$nexusFlag = if ($Nexus) { "-p:NexusEdition=true" } else { "-p:NexusEdition=false" }
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null

foreach ($rid in "win-x64", "linux-x64") {
    $dir = Join-Path $out "JunimoManager-$Version-$rid$tag"
    # Linux: invariant globalization so the app does not depend on libicu being installed on every distribution.
    $extra = if ($rid -like "linux*") { "-p:InvariantGlobalization=true" } else { "-p:InvariantGlobalization=false" }
    dotnet publish src/SdvMods.App -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version $nexusFlag $extra -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Get-ChildItem $dir -Filter *.pdb | Remove-Item -Force
}

# Windows: zip
Compress-Archive -Path (Join-Path (Join-Path $out "JunimoManager-$Version-win-x64$tag") "*") -DestinationPath (Join-Path $out "JunimoManager-$Version-win-x64$tag.zip")

# Linux / Steam Deck: zip (Nexus Mods does not accept tar) with the installer script and icon next to the binary
$linux = Join-Path $out "JunimoManager-$Version-linux-x64$tag"
Copy-Item scripts/install.sh $linux
Copy-Item src/SdvMods.App/Assets/app.png (Join-Path $linux "JunimoManager.png")
# A small .NET tool writes the zip so the Unix permissions (executable bit) survive; Compress-Archive would lose them.
dotnet run --project tools/MakeZip -- $linux (Join-Path $out "JunimoManager-$Version-linux-x64$tag.zip")
if ($LASTEXITCODE -ne 0) { throw "zip creation failed" }

Get-ChildItem $out -File | ForEach-Object { "{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
