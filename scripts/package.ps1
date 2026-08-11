<#
  Packages Callsign for distribution:
    1. builds the web UI            (skip with -SkipUi if ui/dist is already built, e.g. in WSL)
    2. self-contained publish       (bundles the .NET runtime — runs on any Windows PC, no install)
    3. portable zip                 (unzip and run Callsign.exe)
    4. installer                    (only if Inno Setup 6 is installed)

  Usage:
    pwsh scripts/package.ps1                 # full package
    pwsh scripts/package.ps1 -SkipUi         # reuse an existing ui/dist (build it in WSL first)

  Note (this repo's WSL/Windows split): build the UI inside WSL for speed
  (`cd ui && npm run build`), then run this with -SkipUi from Windows.
#>
param(
  [string]$OutDir = "$PSScriptRoot\..\dist",
  [string]$Configuration = "Release",
  [switch]$SkipUi
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$publish = Join-Path $OutDir "callsign-standalone"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $SkipUi) {
  Push-Location (Join-Path $root "ui")
  npm install; if ($LASTEXITCODE) { throw "npm install failed" }
  npm run build; if ($LASTEXITCODE) { throw "npm run build failed" }
  Pop-Location
}

Write-Host "Publishing self-contained app..."
dotnet publish (Join-Path $root "app\Callsign.Desktop\Callsign.Desktop.csproj") `
  -c $Configuration -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

Write-Host "Zipping portable build..."
$zip = Join-Path $OutDir "Callsign-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  Write-Host "Building installer with Inno Setup..."
  & $iscc (Join-Path $root "installer\Callsign.iss") "/DSourceDir=$publish" "/O$OutDir"
} else {
  Write-Host "Inno Setup 6 not found (https://jrsoftware.org/isdl.php) — skipped installer."
}

Write-Host "`nDone. Artifacts in $OutDir :"
Write-Host "  - callsign-standalone\   (self-contained folder)"
Write-Host "  - Callsign-portable.zip  (unzip and run Callsign.exe)"
if ($iscc) { Write-Host "  - Callsign-Setup-*.exe   (installer)" }
