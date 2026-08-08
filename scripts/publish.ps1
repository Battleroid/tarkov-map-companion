# Produces a self-contained, single-file Windows build that needs NO .NET install to run.
# Output: <repo>\publish\TarkovMapCompanion.exe
#
#   scripts\publish.ps1
#   scripts\publish.ps1 -Version v0.3.2
param([string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\TarkovMapCompanion\TarkovMapCompanion.csproj"
$out  = Join-Path $root "publish"

# Stamp the version from the tag. Left off, the build carries whatever is checked into the csproj,
# and then the About window and the crash log name a build nobody released.
$versionArgs = @()
if ($Version) {
    $numeric = ($Version -replace '^v', '') -replace '[-+].*$', ''
    if ($numeric -notmatch '^\d+(\.\d+){0,3}$') { throw "'$Version' has no version number I can build with" }
    $versionArgs = @("-p:Version=$numeric", "-p:InformationalVersion=$Version")
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing self-contained single-file win-x64 build..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
  @versionArgs `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The native HarfBuzz package drops a 20 MB .pdb next to the exe; it is not wanted in a release.
Remove-Item (Join-Path $out "*.pdb") -ErrorAction SilentlyContinue

$exe = Join-Path $out "TarkovMapCompanion.exe"
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Done. $exe ($mb MB)" -ForegroundColor Green
Write-Host "That single file is the whole app: maps and exit data are embedded, so it runs offline." -ForegroundColor Green
