# Project Callsign

A desktop career & airline-management companion for **Microsoft Flight Simulator 2024**.
Pick jobs, fly them in the sim, get scored and paid — and run a company (aircraft,
staff, bases, trade, finance) that keeps ticking whether or not the sim is running.

This is an **original, clean-room** application. It reimplements a *genre*, not any
existing product. All code, data, and art here are our own or from openly licensed
sources (see [`docs/adr/0001-stack.md`](docs/adr/0001-stack.md)).

> **Status:** Phase 1 complete — the core loop plays end to end. Scan the aircraft you
> already own in the sim, take a cargo job (the reward is quoted and locked on accept),
> fly it, land, and get paid through an itemised ledger. Works live against MSFS or
> against a synthetic telemetry source with the sim closed. See
> [`docs/phase-1-plan.md`](docs/phase-1-plan.md).

## Repository layout

```
Callsign.slnx                     Solution (XML .slnx format)
Directory.Build.props / .targets  Shared build settings; native SimConnect.dll copy
global.json                       Pins the .NET SDK
src/
  Callsign.Core/                  Domain, ledger-backed economy, jobs, settlement,
                                  flight tracker, airport + aircraft data (net10.0)
  Callsign.Host/                  ASP.NET Core host: REST API + telemetry WebSocket;
                                  serves the UI. Multi-targets net10.0 (portable) and
                                  net10.0-windows (live SimConnect)
  Callsign.SimConnect/            Portable telemetry abstraction + synthetic source (net10.0)
  Callsign.SimConnect.Windows/    Real SimConnect adapter (net10.0-windows, Windows-only)
ui/                               React + TypeScript front end (Vite)
app/
  Callsign.SimPoc/                Console SimConnect proof-of-concept (brief §10.3)
tests/                            Core, Host, and SimConnect unit/integration tests
docs/                             ADRs, the Phase 1 plan, domain notes
scripts/fetch-simconnect.ps1      Copies SimConnect DLLs out of the installed MSFS SDK
vendor/simconnect/                SimConnect binaries land here (git-ignored)
```

## Prerequisites

- **.NET 10 SDK.** See ADR 0001 for why 10 rather than the brief's 8.
- **Node.js 18+** to build the UI (run it from WSL — see below).
- **Windows + MSFS 2024** for *live* telemetry. Everything else — the domain, economy,
  REST API, UI, and tests — builds and runs against the synthetic source on any OS.
- For live telemetry: install the **MSFS 2024 SDK**, then run
  `scripts/fetch-simconnect.ps1` to copy the two SimConnect DLLs into `vendor/`. Without
  them the Windows adapter compiles as a stub and the app falls back to the synthetic source.

### The WSL/Windows split

The source lives on the WSL2 filesystem, but SimConnect is Windows-only, so the .NET
solution is built and run **from Windows** (the same box MSFS runs on), editing the files
over the `\\wsl.localhost\...` share. Two practical notes:

- **Build the UI inside WSL** (`npm` on the native Linux filesystem is seconds; over the
  9p share it's minutes).
- **Run the Host from a local disk.** With the repo on the `\\wsl.localhost` share, publish
  to a local folder and run the published build there — starting the web host with its
  content root on the share is slow.

## Build & run

### The app (career loop)

```bash
# 1. Build the web UI (inside WSL — native filesystem)
cd ui && npm install && npm run build

# 2. Publish the Host to a local folder. For LIVE MSFS telemetry, target Windows:
dotnet publish src/Callsign.Host -c Release -f net10.0-windows -o C:\callsign
#    (or -f net10.0 for the portable build — always synthetic telemetry, any OS)

# 3. Put the UI where the Host can serve it, then run:
#    copy ui/dist into C:\callsign\wwwroot  (or pass --Ui:Path=...\ui\dist)
C:\callsign\Callsign.Host.exe --urls http://localhost:5199
```

Then open <http://localhost:5199>. (For quick iteration you can instead
`dotnet run --project src/Callsign.Host -f net10.0-windows` when the repo is on a local
disk, and run the UI dev server with `cd ui && npm run dev`.)

### Fly a leg

1. Start **MSFS 2024** and load into an aircraft on a runway.
2. Run the **Windows** Host (`-f net10.0-windows`). The **Flight** tab shows a live link
   and streams your altitude / airspeed / vertical speed.
3. Start a career, accept a cargo job on the **Jobs** board, and hit **Begin flight**.
4. Fly to the destination and land. Callsign settles the job automatically and pays you,
   itemised (base reward + landing bonus), and the flight and every dollar show up in the
   **Logbook**.

With the sim closed the same loop works against the synthetic source (the HUD flies a
canned profile), so the whole app is playable and testable with MSFS off.

### Tests / PoC

```bash
dotnet test Callsign.slnx                            # all tests
dotnet run --project app/Callsign.SimPoc             # real SimConnect if present, else fake
dotnet run --project app/Callsign.SimPoc -- --fake   # force the synthetic source
```

## Documentation

Decisions live in [`docs/adr/`](docs/adr/); the build order and status are in
[`docs/phase-1-plan.md`](docs/phase-1-plan.md). Product intent comes from the project
brief — the "do better than the incumbent" thesis is the point of the project, not
optional polish.
