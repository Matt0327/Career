# Project Callsign

A desktop career & airline-management companion for **Microsoft Flight Simulator 2024**.
Pick jobs, fly them in the sim, get scored and paid — and run a company (aircraft,
staff, bases, trade, finance) that keeps ticking whether or not the sim is running.

This is an **original, clean-room** application. It reimplements a *genre*, not any
existing product. All code, data, and art here are our own or from openly licensed
sources (see [`docs/adr/0001-stack.md`](docs/adr/0001-stack.md)).

> **Status:** Phase 0 — foundations. Repo skeleton, stack decision, and the
> SimConnect proof-of-concept are in place. Nothing is a shippable feature yet.

## Repository layout

```
Callsign.slnx                     Solution (XML .slnx format)
Directory.Build.props             Shared build settings
global.json                       Pins the .NET SDK
src/
  Callsign.SimConnect/            Portable telemetry abstraction + fake source (net10.0)
  Callsign.SimConnect.Windows/    Real SimConnect adapter (net10.0-windows, Windows-only)
app/
  Callsign.SimPoc/                Console proof-of-concept (brief §10.3)
tests/
  Callsign.SimConnect.Tests/      Unit tests (run on any OS)
docs/adr/                         Architecture Decision Records
scripts/fetch-simconnect.ps1      Copies SimConnect DLLs out of the installed MSFS SDK
vendor/simconnect/                SimConnect binaries land here (git-ignored)
.github/workflows/ci.yml          CI
```

## Prerequisites

- **.NET 10 SDK** (LTS). See ADR 0001 for why 10 rather than the brief's 8.
- **Windows + MSFS 2024** for anything that touches SimConnect. The portable
  library and tests build and run on any OS; the Windows adapter and the PoC's
  *real* mode need Windows and the SimConnect binaries.
- For real telemetry: the **MSFS 2024 SDK** installed, then run
  `scripts/fetch-simconnect.ps1` to copy the two SimConnect DLLs into `vendor/`.

### A note on the WSL/Windows split

The source lives on the WSL2 filesystem, but SimConnect is Windows-only, so the
.NET solution is built and run **from Windows** (the same box MSFS runs on),
editing the files over the `\\wsl.localhost\...` share. See ADR 0001.

## Build & run

```bash
# Portable library + tests — any OS
dotnet test tests/Callsign.SimConnect.Tests/Callsign.SimConnect.Tests.csproj

# Everything (Windows) — builds in "stub" mode until SimConnect binaries exist
dotnet build Callsign.slnx

# The proof-of-concept
dotnet run --project app/Callsign.SimPoc            # real if SimConnect present, else fake
dotnet run --project app/Callsign.SimPoc -- --fake  # force the synthetic source
```

## Documentation

Decisions live in [`docs/adr/`](docs/adr/). Product intent and requirements come
from the project brief; the "do better than the incumbent" thesis is the point of
the project, not optional polish.
