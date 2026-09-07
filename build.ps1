# Packs the mod into dist\ and, with -Deploy, copies it into the game's Mods folder.
#   .\build.ps1
#   .\build.ps1 -Deploy
#   .\build.ps1 -Bundle -Deploy     # rebuild the Unity asset bundle first
#   .\build.ps1 -NoCode             # skip the C# build (this mod has none)
#
# Three things ship: the XML in pack\Config, the Unity asset bundle in pack\Resources, and
# Werewolf.dll from src\. Only the first two make the creature: an animal's animator controller
# travels inside its prefab and the material is Unity's own Standard shader, so nothing has to be
# loaded at runtime. The DLL is a TUNING TOOL - the 'werewolf' console command - and the mod works
# without it. -NoCode skips building it and packs whatever is already in pack\.

param(
    [switch]$Deploy,
    [switch]$Bundle,
    [switch]$NoCode,
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\7 Days To Die"
)

$ErrorActionPreference = "Stop"

$modDir = $PSScriptRoot
$name = Split-Path $modDir -Leaf
$repo = Split-Path (Split-Path $modDir -Parent) -Parent
$pack = Join-Path $modDir "pack"
$modInfo = Join-Path $pack "ModInfo.xml"

$version = ([xml](Get-Content $modInfo)).xml.Version.value
Write-Host "Packing $name $version"

# The clips. tools\sync-sounds.py rewrites the <AudioClip> lines in sounds.xml to match the role
# folders under art\sound and compares the files with the manifest of the last bundle build. With
# -Bundle it records the manifest and the bundle is built from exactly those files; without -Bundle
# a change under art\sound means the bundle in pack\ is stale, and deploying it would ship old
# clips with a fresh xml - so the build stops and says so.
$global:LASTEXITCODE = 0
if ($Bundle) { python (Join-Path $modDir "tools\sync-sounds.py") --mark-built }
else         { python (Join-Path $modDir "tools\sync-sounds.py") --check }
if ($LASTEXITCODE -eq 2) { throw "sounds.xml needs a hand-made fix, see above" }
if ($LASTEXITCODE -ne 0 -and -not $Bundle) { throw "art\sound changed since the bundle was built - run .\build.ps1 -Bundle -Deploy" }

if ($Bundle) {
    # build-asset-bundle.ps1 throws on failure; $LASTEXITCODE is not its verdict. It is whatever
    # the last NATIVE command anywhere in the session left behind (Unity itself is run through
    # Start-Process, which never sets it), and a stale non-zero value here once declared a
    # perfectly good 10 MB bundle "failed" and skipped the deploy.
    $global:LASTEXITCODE = 0
    # -Prefab always. In build-asset-bundle.ps1 the audio copy (art\sound -> Assets\Werewolf\Audio,
    # renamed ww_<role><n>) sits inside the -Prefab branch, so without it the bundle is rebuilt from
    # whatever clips the Unity project already had: the owner replaced the howl and attack clips,
    # rebuilt three times, and heard the old ones every time. Rebuilding the prefab as well costs
    # a minute and nothing else.
    & (Join-Path $repo "tools\build-asset-bundle.ps1") -Mod $name -GameDir $GameDir -Prefab
    if ($LASTEXITCODE -ne 0) { throw "asset bundle build failed" }
}

# The csproj's own DeployMod target drops the DLL into pack\, so this has to run before packing
if (-not $NoCode) {
    $proj = Join-Path $modDir "src\$name.csproj"
    if (Test-Path $proj) {
        Write-Host "Building $name.dll"
        dotnet build $proj -c Release -v minimal /p:GameDir=$GameDir
        if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
    }
}

$dll = Join-Path $pack "$name.dll"
if (-not (Test-Path $dll)) {
    Write-Host "note: no $name.dll in pack - the creature still works, the 'werewolf' console command will not exist."
}

# The bundle is named after the mod, lower-cased, and entityclasses.xml asks for it by that name.
$bundlePath = Join-Path $pack "Resources\$($name.ToLower()).unity3d"
if (-not (Test-Path $bundlePath)) {
    Write-Host "WARNING: no asset bundle at $bundlePath - the entity will fail to spawn."
    Write-Host "         Build it with .\build.ps1 -Bundle, or see NOTES.md."
}

$dist = Join-Path $modDir "dist"
$staging = Join-Path $dist "staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $staging $name) | Out-Null

# One folder at the archive root: that is what Vortex and the Mod Launcher expect
Copy-Item -Path (Join-Path $pack "*") -Destination (Join-Path $staging $name) -Recurse
Get-ChildItem -Path (Join-Path $staging $name) -Recurse -Filter ".gitkeep" | Remove-Item -Force
foreach ($doc in @("README.md", "CHANGELOG.md", "LICENSE")) {
    $path = Join-Path $modDir $doc
    if (Test-Path $path) { Copy-Item $path (Join-Path $staging $name) }
}

$zip = Join-Path $dist "$name-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging $name) -DestinationPath $zip

if ($Deploy) {
    $target = Join-Path $GameDir "Mods\$name"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path (Join-Path $staging "$name\*") -Destination $target -Recurse
    Write-Host "Deployed to $target"
}

Remove-Item $staging -Recurse -Force
Write-Host "Done: $zip"
