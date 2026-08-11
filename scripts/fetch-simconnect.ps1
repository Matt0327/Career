#requires -Version 5.1
<#
.SYNOPSIS
    Copies the SimConnect binaries the Windows adapter needs out of an installed
    MSFS 2024 SDK into /vendor/simconnect (git-ignored).

.DESCRIPTION
    Run this once after installing the MSFS 2024 SDK. To install the SDK: launch
    MSFS 2024, Options > General > Developers > enable Developer Mode, then use the
    Dev Mode menu bar > Help > "SDK Installer" and install at least the Core /
    SimConnect components.

    After this script succeeds, rebuild:  dotnet build Callsign.slnx
    and the real SimConnectTelemetrySource is compiled instead of the stub.

.PARAMETER SdkRoot
    Path to the MSFS SDK. Defaults to $env:MSFS_SDK, then common install locations.
#>
[CmdletBinding()]
param(
    [string]$SdkRoot = $env:MSFS_SDK,
    [string]$Destination = (Join-Path $PSScriptRoot '..\vendor\simconnect')
)

$ErrorActionPreference = 'Stop'

$candidates = @()
if ($SdkRoot) { $candidates += $SdkRoot }
$candidates += @('C:\MSFS 2024 SDK', 'C:\MSFS SDK', 'D:\MSFS 2024 SDK', 'D:\MSFS SDK')

$sdk = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sdk) {
    throw "MSFS SDK not found. Install it from inside MSFS, or pass -SdkRoot. Looked in: $($candidates -join ', ')"
}
Write-Host "Using SDK: $sdk"

$managed = Get-ChildItem -Path $sdk -Recurse -Filter 'Microsoft.FlightSimulator.SimConnect.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1
$native = Get-ChildItem -Path $sdk -Recurse -Filter 'SimConnect.dll' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch 'Microsoft\.FlightSimulator' } |
    Select-Object -First 1

if (-not $managed) { throw "Managed 'Microsoft.FlightSimulator.SimConnect.dll' not found under $sdk" }
if (-not $native)  { throw "Native 'SimConnect.dll' not found under $sdk" }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item $managed.FullName (Join-Path $Destination 'Microsoft.FlightSimulator.SimConnect.dll') -Force
Copy-Item $native.FullName  (Join-Path $Destination 'SimConnect.dll') -Force

Write-Host "Copied:"
Write-Host "  $($managed.FullName)"
Write-Host "  $($native.FullName)"
Write-Host "-> $((Resolve-Path $Destination).Path)"
Write-Host ""
Write-Host "Next: dotnet build Callsign.slnx"
