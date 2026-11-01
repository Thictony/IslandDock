# Builds IslandDock-Setup.exe: one file to send to people. It carries IslandDock published self-contained (the .NET
# runtime inside, so nothing to install first) plus the uninstaller, and runs on any 64-bit Windows 10 or 11.
#   powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$work = Join-Path $env:TEMP "islandsetup-build"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$app = Join-Path $work "app"; $obj = Join-Path $work "src"
New-Item -ItemType Directory -Force $app, $obj | Out-Null

# Build outside the source folder (its own obj/bin stay out of the way), as the island's build does.
foreach ($p in "IslandDock", "IslandSetup") {
    robocopy (Join-Path $root $p) (Join-Path $obj $p) /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying $p failed" }
}
$global:LASTEXITCODE = 0

"Publishing IslandDock (self-contained)..."
dotnet publish (Join-Path $obj "IslandDock\IslandDock.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -o $app -v q -nologo
if ($LASTEXITCODE) { throw "IslandDock publish failed" }
Get-ChildItem $app -Filter *.pdb | Remove-Item

"Building the uninstaller..."
dotnet build (Join-Path $obj "IslandSetup\IslandSetup.csproj") -c Release -o (Join-Path $work "uninstall") -v q -nologo
if ($LASTEXITCODE) { throw "uninstaller build failed" }
Copy-Item (Join-Path $work "uninstall\IslandSetup.exe") (Join-Path $app "Uninstall IslandDock.exe")

"Packing..."
$zip = Join-Path $work "payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

"Building the installer..."
Remove-Item (Join-Path $obj "IslandSetup\obj") -Recurse -Force -ErrorAction SilentlyContinue
dotnet build (Join-Path $obj "IslandSetup\IslandSetup.csproj") -c Release -o (Join-Path $work "setup") -p:PayloadZip="$zip" -v q -nologo
if ($LASTEXITCODE) { throw "installer build failed" }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist "IslandDock-Setup.exe"
Copy-Item (Join-Path $work "setup\IslandSetup.exe") $out -Force
"Done: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
