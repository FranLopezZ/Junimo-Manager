# Builds distributable packages for Windows and Linux/Steam Deck into ./release
# Usage: powershell -File scripts/build-release.ps1 [-Version 0.5.2]
param([string]$Version = "0.5.2")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$out = Join-Path $root "release"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $out | Out-Null

foreach ($rid in "win-x64", "linux-x64") {
    $dir = Join-Path $out "JunimoManager-$Version-$rid"
    # Linux: invariant globalization so the app does not depend on libicu being installed on every distribution.
    $extra = if ($rid -like "linux*") { "-p:InvariantGlobalization=true" } else { "-p:InvariantGlobalization=false" }
    dotnet publish src/SdvMods.App -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version $extra -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Get-ChildItem $dir -Filter *.pdb | Remove-Item -Force
}

# Windows: zip
Compress-Archive -Path (Join-Path (Join-Path $out "JunimoManager-$Version-win-x64") "*") -DestinationPath (Join-Path $out "JunimoManager-$Version-win-x64.zip")

# Linux / Steam Deck: zip (Nexus Mods does not accept tar) with the installer script and icon next to the binary
$linux = Join-Path $out "JunimoManager-$Version-linux-x64"
Copy-Item scripts/install.sh $linux
Copy-Item src/SdvMods.App/Assets/app.png (Join-Path $linux "JunimoManager.png")
# A small .NET tool writes the zip so the Unix permissions (executable bit) survive; Compress-Archive would lose them.
dotnet run --project tools/MakeZip -- $linux (Join-Path $out "JunimoManager-$Version-linux-x64.zip")
if ($LASTEXITCODE -ne 0) { throw "zip creation failed" }

Get-ChildItem $out -File | ForEach-Object { "{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
