# Produces a self-contained, single-file Windows build that needs NO .NET install to run.
# Output: <repo>\publish\TarkovMapCompanion.exe
#
#   scripts\publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\TarkovMapCompanion\TarkovMapCompanion.csproj"
$out  = Join-Path $root "publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing self-contained single-file win-x64 build..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
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
