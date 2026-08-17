<#
  Cut and publish a Callsign release in one step:
    1. build the web UI (in WSL) + the app, and pack the Velopack installer + update feed (package.ps1)
    2. upload the whole feed to the public 'releases' bucket in Supabase
  ...so every installed copy auto-updates on its next launch. Run this once your changes are on main.

  Reads SUPABASE_URL + SUPABASE_SERVICE_KEY from a git-ignored .env in the repo root (or the environment).
  The service_role key is a secret -- keep it in .env, never commit it.

  Usage:  powershell -File scripts\release.ps1            # build UI + app, pack, upload
          powershell -File scripts\release.ps1 -SkipUi    # if ui\dist is already current
#>
param(
  [string]$OutDir = "$PSScriptRoot\..\dist",
  [switch]$SkipUi
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# --- credentials from .env or the environment ---
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
  Get-Content $envFile | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$') {
      [Environment]::SetEnvironmentVariable($matches[1], $matches[2].Trim('"').Trim("'"))
    }
  }
}
$url = $env:SUPABASE_URL; $key = $env:SUPABASE_SERVICE_KEY
if (-not $url -or -not $key) { throw "Set SUPABASE_URL and SUPABASE_SERVICE_KEY (environment or a .env in the repo root)." }

# --- build the web UI in WSL (its toolchain lives there on this machine) unless told to skip ---
if (-not $SkipUi) {
  Write-Host "Building the web UI (WSL)..."
  wsl.exe -d ubuntu -- bash -lc 'cd ~/Career/ui && npm run build'
  if ($LASTEXITCODE) { throw "UI build failed - build it manually or re-run with -SkipUi." }
}

# --- build the app + pack the installer and update feed (dist\releases) ---
& "$PSScriptRoot\package.ps1" -OutDir $OutDir -SkipUi
$releases = Join-Path $OutDir "releases"
if (-not (Test-Path $releases)) { throw "No releases folder - the Velopack pack step didn't run (is vpk installed?)." }

# --- upload the whole feed to the public 'releases' bucket (overwrite each file) ---
$headers = @{ apikey = $key; Authorization = "Bearer $key"; 'x-upsert' = 'true' }
foreach ($f in Get-ChildItem $releases -File) {
  $dest = "$url/storage/v1/object/releases/$($f.Name)"
  Write-Host ("  uploading {0} ({1} MB)..." -f $f.Name, [math]::Round($f.Length / 1MB, 1))
  Invoke-RestMethod -Uri $dest -Method Post -Headers $headers -InFile $f.FullName -ContentType 'application/octet-stream' | Out-Null
}
Write-Host "`nReleased. Installed copies will pick it up on their next launch."
