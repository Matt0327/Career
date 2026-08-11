# ADR 0001 — Technology stack and process architecture

- **Status:** Accepted
- **Date:** 2026-08-11
- **Deciders:** Project owner + engineering
- **Supersedes:** —

## Context

We are building an original, clean-room career/economy companion for Microsoft
Flight Simulator 2024 (see the project brief). Two constraints dominate the stack
decision:

1. **SimConnect is the foundation and the biggest technical risk.** The only
   first-class binding to the sim is the native Windows SimConnect API and its
   Microsoft-supplied managed interop (`Microsoft.FlightSimulator.SimConnect.dll`).
   The Python and Node wrappers are unreliable across sim updates. This pins the
   sim-facing core to **.NET on Windows**.

2. **The machine reality (observed 2026-08-11).** The repository lives on the
   **WSL2 (Ubuntu 24.04)** filesystem at `/home/matt/Career`, reachable from
   Windows at `\\wsl.localhost\ubuntu\home\matt\Career`. The Windows host has
   Node 24, git, and **MSFS 2024 installed** (Store package `Microsoft.Limitless`),
   but **no .NET SDK** and **no MSFS/SimConnect SDK** yet. WSL has only an older
   Node 18 and no .NET.

Everything below follows from reconciling "the code is in WSL" with "SimConnect is
Windows-only."

## Decision

### 1. Runtime & language: **.NET (C#)**, targeting **.NET 10 (LTS)**

C#/.NET for the core service is non-negotiable, per constraint 1. On version: the
brief names .NET 8, but as of this date .NET 8's mainstream support ends
**November 2026** (~3 months out) and .NET 9 (STS) is already end-of-life. **.NET 10**
is the current LTS, supported through **November 2028**. SimConnect's managed
interop is a thin P/Invoke wrapper that works identically on .NET 10. For a
multi-month new build, starting on an LTS that is about to expire is a needless
liability, so we target **.NET 10**. This is the one deliberate deviation from the
brief; it is a one-line change in `global.json` + the TFMs if we must revert to 8.

### 2. Sim binding: **official SimConnect managed interop, behind an interface**

- The real telemetry source (`SimConnectTelemetrySource`, `net10.0-windows`) uses
  Microsoft's own `Microsoft.FlightSimulator.SimConnect.dll` + native
  `SimConnect.dll`, obtained from the **official MSFS 2024 SDK** (installed from
  inside the sim). These are **not committed** to the repo — `scripts/fetch-simconnect.ps1`
  copies them into `vendor/simconnect/` (git-ignored), and the adapter references
  them by `HintPath`.
- **The sim binding is abstracted behind `ISimTelemetrySource`.** A portable
  `FakeTelemetrySource` replays a synthetic flight with no SimConnect dependency.
  This is not a toy: it is how we satisfy brief acceptance criterion #6 ("starting
  with MSFS closed works fine; every company-loop feature is fully usable"), how
  the whole app and CI run on machines without the sim, and how we test the
  economy without flying.
- When the SimConnect binaries are absent, the Windows adapter compiles as a
  **stub that throws**, so the entire solution still builds anywhere; callers fall
  back to the fake source. The real implementation lights up the moment the DLLs
  exist.

### 3. UI shell: **ASP.NET Core (Kestrel) host serving a React SPA over REST + WebSocket, wrapped in a native WebView2 window — NOT Tauri, NOT Electron**

The brief prefers Tauri with an ASP.NET-host fallback. We are **electing the
fallback as the primary**, deliberately, for three reasons:

1. **The companion web view (brief §5.4) makes a browser-served SPA mandatory
   anyway.** If the primary UI *is* a Kestrel-served SPA, the LAN companion is
   nearly free — same backend, same responsive UI. With Tauri we would build the
   desktop UI in Tauri **and** a second web UI for the companion, which cuts
   against "one coherent product" (§3.8) and doubles UI work.
2. **Toolchain cost.** Tauri is Rust-first; pairing it with a .NET backend means
   running .NET as a sidecar process regardless, i.e. three languages
   (Rust + C# + TS) and a Rust toolchain that isn't installed — and cross-building
   a Windows Tauri app from a WSL-hosted repo is awkward. The Kestrel approach is
   two languages (C# + TS), no Rust.
3. **We still get a native desktop window** by pointing a thin **WebView2** host
   (or Photino.NET) at the local Kestrel URL — a real app window, no browser
   chrome, no Electron-sized runtime.

Rejected: **Electron** (footprint, and it still wouldn't host .NET naturally).
Rejected: **Tauri** (Rust toolchain + duplicate UI for the companion + WSL
cross-build friction), for the reasons above.

### 4. Persistence: **SQLite via EF Core**

Single file, trivial backup/restore (brief §5.1, Phase 5), no server. The
`LedgerEntry` table is the single source of truth for money (brief §6); all
balances and P&L are queries over it.

### 5. Background work: **hosted services in the core process**

The simulation tick, job generation, and autonomous/offline flight progression run
as `IHostedService`s in the Kestrel process, advancing on wall-clock time so that
closing the app for 8 hours and reopening equals leaving it running (acceptance #4).

### 6. Solution layout

```
Callsign.SimConnect          net10.0          telemetry abstraction + fake source, identity helpers
Callsign.SimConnect.Windows  net10.0-windows  real SimConnect adapter (stub when DLLs absent)
Callsign.SimPoc              net10.0-windows  console PoC (brief §10.3)
Callsign.SimConnect.Tests    net10.0          unit tests (any OS)
```

Future (Phase 1+): `Callsign.Core` (domain + economy + EF Core), `Callsign.Host`
(Kestrel: REST + WS + static SPA), `ui/` (React + TS + Vite). Deferred until the
PoC is proven, per the brief's "get the PoC solid before anything else exists."

### 7. Build & run boundary (the WSL/Windows split)

The .NET solution is **built and run from Windows**, editing files over the
`\\wsl.localhost\...` share, because SimConnect and MSFS are Windows-only. The
portable library + tests also build in WSL/Linux CI. The React UI can be built with
Windows Node 24 (WSL's Node 18 is too old). This split is a first-class fact of the
project, documented here and in the README.

## Consequences

**Positive**
- The companion (§5.4) is almost free; one UI, one model (§3.8).
- The whole product is usable and testable with the sim closed (#6), and CI is
  green without any Microsoft binaries.
- No Rust; two languages; smaller moving-part count than Tauri-plus-sidecar.
- Targeting an LTS with three years of runway.

**Negative / risks**
- WebView2 desktop shell is slightly more assembly than Tauri's batteries-included
  window (mitigated: Photino.NET or a ~100-line WinForms+WebView2 host).
- Building over the WSL 9p share from Windows has slower file IO than a native
  Windows checkout (acceptable; can relocate later if it bites).
- The real SimConnect adapter cannot be compiled or live-tested until the .NET SDK
  and SimConnect binaries are present. Until then it is **written but unverified**,
  and the PoC runs in fake mode.

## Open questions (tracked, not blocking)

- **.NET version** — confirm 10 vs the brief's 8 with the owner.
- **SimConnect sourcing** — official SDK (preferred) vs a managed OSS client. A
  pure-C# reimplementation would remove the native dependency but carries the same
  "breaks across sim updates" risk the brief warns about for other wrappers.
- **Desktop shell** — WebView2 host vs Photino.NET; decide when we build the Host.

## Alternatives considered

| Option | Why not |
|---|---|
| Python/Node SimConnect wrapper | Unreliable across sim updates (brief §5.1); no first-class binding. |
| Tauri (Rust) shell | Rust toolchain, three-language stack, duplicate companion UI, awkward WSL→Windows cross-build. |
| Electron | Runtime footprint; doesn't host .NET naturally. |
| .NET 8 (per brief) | LTS ends Nov 2026; short runway for a new project. Kept as a trivial fallback. |
| SQL Server / Postgres | Needs a server; overkill for a single-user desktop app; SQLite backs up as one file. |
