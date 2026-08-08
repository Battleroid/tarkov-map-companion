# Build a self-contained release and (optionally) publish it as a GitHub release.
#
#   scripts\release.ps1 -Version v0.1.0                 # build only
#   scripts\release.ps1 -Version v0.1.0 -Publish        # build + create GitHub release
#   scripts\release.ps1 -Version v0.1.0 -Publish -Draft # ...as a draft
#
# Pushing a v* tag also triggers the Release workflow on GitHub Actions, which does this
# automatically and is the normal path. This script is for cutting a release from your machine.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Publish,
    [switch]$Draft
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1) tests, because a release that skipped them is not worth cutting
Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root "TarkovMapCompanion.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "tests failed; not releasing" }

# 2) build the self-contained single-file app, stamped with the version being released
& (Join-Path $PSScriptRoot "publish.ps1") -Version $Version

# 3) name it after the version, which is what the release asset is called
$asset = Join-Path $root ("TarkovMapCompanion-{0}-win-x64.exe" -f $Version)
if (Test-Path $asset) { Remove-Item $asset }
Copy-Item (Join-Path $root "publish\TarkovMapCompanion.exe") $asset

$mb = [math]::Round((Get-Item $asset).Length / 1MB, 1)
Write-Host "Packaged $asset ($mb MB)" -ForegroundColor Green

# 4) optionally cut the GitHub release
if ($Publish) {
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
    if (-not (Test-Path $gh)) { throw "gh CLI not found; install it or run without -Publish." }

    $relArgs = @("release", "create", $Version, $asset, "--title", "Tarkov Map Companion $Version")
    if ($NotesFile) { $relArgs += @("--notes-file", $NotesFile) }
    elseif ($Notes) { $relArgs += @("--notes", $Notes) }
    else            { $relArgs += "--generate-notes" }
    if ($Draft) { $relArgs += "--draft" }

    & $gh @relArgs
    Write-Host "Release $Version created." -ForegroundColor Green
} else {
    Write-Host "Built $asset. Re-run with -Publish to create the GitHub release (or push a v* tag)." -ForegroundColor Cyan
}
