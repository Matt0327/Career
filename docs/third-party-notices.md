# Third-party components & redistribution

Callsign is an original, clean-room application. Everything shipped is either our own code,
an openly-licensed dependency, or a vendor component that is explicitly meant to be
redistributed with add-ons. This file records what we bundle, under what terms, and the one
item to confirm before a public release.

## What ships in a Callsign build

| Component | Role | License / terms | Redistribution |
|---|---|---|---|
| **.NET runtime** (bundled in the self-contained build) | Runs the app | MIT | ✅ Redistributable |
| **ASP.NET Core, EF Core** | In-process web host + data access | MIT | ✅ |
| **SQLitePCLRaw** (`bundle_e_sqlite3`) | Native SQLite provider | Apache-2.0 | ✅ |
| **SQLite** (via the bundle) | The save database engine | **Public domain** | ✅ |
| **React, React-DOM** | UI | MIT | ✅ |
| **Vite, @vitejs/plugin-react, TypeScript** | UI build tooling (not shipped in the app) | MIT / Apache-2.0 | ✅ |
| **Microsoft.Web.WebView2 (SDK + Loader)** | Embeds the UI in a native window | Microsoft — redistributable with apps | ✅ |
| **Microsoft Edge WebView2 Runtime** | Renders the window (installed on the user's PC) | Microsoft "Evergreen" runtime | Installed via Microsoft's bootstrapper, not bundled |
| **OurAirports data** (bundled, gzipped) | Airports & runways | **Public domain** | ✅ |
| **SimConnect** (`Microsoft.FlightSimulator.SimConnect.dll` + native `SimConnect.dll`) | Talks to MSFS | Microsoft Flight Simulator SDK | ⚠️ See below |

## SimConnect — the one thing to confirm before public release

`Microsoft.FlightSimulator.SimConnect.dll` (managed) and the native `SimConnect.dll` are part of
the **Microsoft Flight Simulator SDK**. Microsoft provides SimConnect precisely so third-party
add-ons can communicate with the simulator, and the client libraries are intended to be shipped
alongside add-ons — this is standard practice across the MSFS add-on ecosystem.

**Action before any public distribution:** read the current MSFS SDK / EULA terms and confirm they
permit redistributing these client libraries with Callsign, and keep that confirmation on file.
This is a licensing check on Microsoft's SDK, not a clean-room concern.

Operationally: these DLLs are **not committed** to this repository. They are copied out of a locally
installed MSFS SDK by [`scripts/fetch-simconnect.ps1`](../scripts/fetch-simconnect.ps1) into
`vendor/simconnect/` (git-ignored). A build without them compiles a stub and falls back to the
synthetic telemetry source.

## Clean-room statement

Callsign reimplements a *genre* (an MSFS career/economy companion), not any existing product. No
competitor's binaries, database files, navigation data, artwork, icons, or assets were decompiled,
extracted, or referenced. All code, UI, naming, and art here are our own; all data comes from the
openly-licensed sources listed above.
