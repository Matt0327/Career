<#
  Packages Callsign for distribution. Runs under Windows PowerShell 5.1 or PowerShell 7.
    1. builds the web UI            (skip with -SkipUi if ui/dist is already built, e.g. in WSL)
    2. self-contained single-file publish (bundles the .NET runtime - one Callsign.exe, no install)
    3. portable zip                 (unzip and run Callsign.exe)
    4. installer                    (only if Inno Setup 6 is installed)
    5. code signing                 (only if a signing cert is configured - see below)

  Usage:
    powershell -File scripts\package.ps1            # full package
    powershell -File scripts\package.ps1 -SkipUi    # reuse an existing ui/dist (build it in WSL first)

  Code signing (optional, off by default): set CALLSIGN_SIGN_THUMBPRINT to a code-signing certificate's
  thumbprint in your Windows cert store, or CALLSIGN_SIGN_PFX (+ CALLSIGN_SIGN_PASS) for a .pfx file. When
  neither is set, signing is skipped and the build ships unsigned (SmartScreen warns on first run).

  Note (this repo's WSL/Windows split): build the UI inside WSL for speed
  (cd ui && npm run build), then run this with -SkipUi from Windows.
#>
param(
  [string]$OutDir = "$PSScriptRoot\..\dist",
  [string]$Configuration = "Release",
  [switch]$SkipUi
)
$ErrorActionPreference = "Stop"
# Split-Path (not Resolve-Path) so a UNC/WSL path stays a plain filesystem path — Resolve-Path prefixes
# it with the PowerShell provider (Microsoft.PowerShell.Core\FileSystem::...), which `dotnet` can't parse.
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $OutDir "callsign-standalone"

# Locate signtool.exe: PATH first, then any x64 build under the Windows 10/11 SDK.
function Find-Signtool {
  $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
  $roots = @("$pf86\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") | Where-Object { Test-Path $_ }
  foreach ($r in $roots) {
    $hit = Get-ChildItem -Path $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if ($hit) { return $hit.FullName }
  }
  return $null
}

# Sign a file if a cert is configured; otherwise a no-op (leaves the build unsigned).
function Invoke-SignFile([string]$Path) {
  $thumb = $env:CALLSIGN_SIGN_THUMBPRINT
  $pfx = $env:CALLSIGN_SIGN_PFX
  if (-not $thumb -and -not $pfx) {
    Write-Host "  (signing skipped - set CALLSIGN_SIGN_THUMBPRINT or CALLSIGN_SIGN_PFX to sign)"
    return
  }
  $signtool = Find-Signtool
  if (-not $signtool) { Write-Warning "  signtool.exe not found (install the Windows SDK) - $Path left unsigned."; return }
  $ts = "http://timestamp.digicert.com"
  if ($thumb) {
    & $signtool sign /sha1 $thumb /fd SHA256 /tr $ts /td SHA256 $Path
  } else {
    & $signtool sign /f $pfx /p $env:CALLSIGN_SIGN_PASS /fd SHA256 /tr $ts /td SHA256 $Path
  }
  if ($LASTEXITCODE) { throw "signtool failed for $Path" }
  Write-Host "  signed $Path"
}

# Single source of truth for the version: the shared Directory.Build.props.
$version = "0.0.0"
$propsPath = Join-Path $root "Directory.Build.props"
if (Test-Path $propsPath) {
  $m = [regex]::Match((Get-Content $propsPath -Raw), '<Version>([^<]+)</Version>')
  if ($m.Success) { $version = $m.Groups[1].Value.Trim() }
}
Write-Host "Packaging Callsign $version"

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $SkipUi) {
  Push-Location (Join-Path $root "ui")
  npm install; if ($LASTEXITCODE) { throw "npm install failed" }
  npm run build; if ($LASTEXITCODE) { throw "npm run build failed" }
  Pop-Location
}

Write-Host "Publishing self-contained single-file app..."
dotnet publish (Join-Path $root "app\Callsign.Desktop\Callsign.Desktop.csproj") `
  -c $Configuration -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

Invoke-SignFile (Join-Path $publish "Callsign.exe")

Write-Host "Zipping portable build..."
$zip = Join-Path $OutDir "Callsign-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal

# Installer (Inno Setup 6) - built only if ISCC is present; version flows in via /DAppVersion.
$pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$iscc = @("$pf86\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  Write-Host "Building installer with Inno Setup..."
  & $iscc (Join-Path $root "installer\Callsign.iss") "/DSourceDir=$publish" "/DAppVersion=$version" "/O$OutDir"
  if ($LASTEXITCODE) { throw "Inno Setup failed" }
  $setup = Join-Path $OutDir "Callsign-Setup-$version.exe"
  if (Test-Path $setup) { Invoke-SignFile $setup }
} else {
  Write-Host "Inno Setup 6 not found (https://jrsoftware.org/isdl.php) - skipped installer."
}

Write-Host "`nDone. Artifacts in $OutDir :"
Write-Host "  - callsign-standalone\   (self-contained folder: Callsign.exe + wwwroot)"
Write-Host "  - Callsign-portable.zip  (unzip and run Callsign.exe)"
if ($iscc) { Write-Host "  - Callsign-Setup-$version.exe   (installer)" }
