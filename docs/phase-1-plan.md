# Phase 1 Plan — The Flying Loop

> **Status: DRAFT for review. Not yet implemented.**
> Phase 0 (foundations + live SimConnect PoC) is complete — commit `9e2eccd`.
> The live PoC connected to MSFS 2024 and read real telemetry off a PC-12.

## Goal (independently playable)

One aircraft, one job type, end to end:

> See a Cargo job at your airport → accept it → fly it in MSFS → get scored on the
> landing → get paid with an **itemised** breakdown → see it in your flight log and
> ledger.

Per the brief: *ship it and fly a leg end-to-end before moving on.*

## What Phase 0 already provides

- `ISimTelemetrySource` — live (`SimConnectTelemetrySource`) + `FakeTelemetrySource`.
- Aircraft **title** capture and `AircraftIdentity` normalisation primitives
  (the live PoC surfaced `"PC-12NGX Cargo - Empty"` — a title ≠ type case).
- Locked decisions: SQLite/EF Core; **ledger is the single source of truth for money**.

## Constraints carried in from the competitive addendum

- **Exceed the free bundled career mode from hour one.** The 10-minute-to-first-flight
  onboarding (brief §8.1) is the acquisition funnel, not polish.
- **Lead with aircraft freedom.** Phase 1 recognises and flies the player's *installed*
  aircraft with no artificial gating; the full rent/buy market is Phase 2.
- **Keep the economy server-ready.** Do **not** bake in "prices are generated locally."
  Put job/price generation behind interfaces (`IJobSource`, `IPriceProvider`) and route
  **every** money movement through the ledger, so a future shared-world server (a Phase 4
  ADR) could become authoritative without reworking Core.

## New components

| Component | TFM | Role |
|---|---|---|
| `Callsign.Core` | net10.0 | Domain, EF Core + SQLite, economy, flight-tracker state machine, aircraft scan, airport data |
| `Callsign.Host` | net10.0 | ASP.NET Core: REST + WebSocket, serves the SPA, wires the telemetry source, hosted services |
| `ui/` | React + TS (Vite) | Minimal: job board, live flight HUD, settlement, flight log |

## Data model (Phase 1 subset of brief §6)

- **Airport** (from OurAirports): icao, name, lat, lon, elevationFt, runways(length, surface).
- **AircraftType**: id, canonicalName, **aliases[]**, category, seats, usefulLoadLbs,
  fuelCapacity, cruiseKtas, minRunwayFt, isInstalled.
- **AircraftInstance**: id, typeId, tail, ownership, locationIcao, hull/engine condition (stubbed).
- **Pilot**: id, name, rank, xp, cash, currentIcao, homeIcao.
- **Job**: id, type(Cargo), originIcao, destIcao, weightLbs, distanceNm, reward, xp, expiresAt, requiredRank.
- **Flight**: id, aircraftInstanceId, jobId, departedAt, arrivedAt, touchdownFpm,
  events(json), fuelUsedLbs, payout, **payoutBreakdown(json)**.
- **LedgerEntry**: id, at, category, amount, description, relatedEntityId — *every* money move.

## Flight-tracking state machine

`Parked → Taxi → Takeoff → Climb → Cruise → Approach → Landing → Shutdown`, driven by
telemetry (on-ground transitions, speed, altitude, VS). Records the **touchdown vertical
speed** (headline score, <~200 fpm = good), plus exceedances (overspeed, stall, over-G,
flap/gear limits), fuel burn, block time, distance, and whether the correct aircraft was used.

> **Telemetry extension:** the Phase 0 struct reads alt/ias/gs/vs/on-ground/title. The
> tracker needs more SimVars (G-force, flaps, gear, stall/overspeed warnings, fuel qty,
> lat/lon). This is an **additive** change to the data definition — validate SimVar
> availability early (step 1e).

## Aircraft detection (brief §3.2 / §4)

Read `InstalledPackagesPath` from MSFS `UserCfg.opt` (Store edition → under the
`Microsoft.Limitless` LocalCache). Scan Official + Community for
`SimObjects/Airplanes/*/aircraft.cfg`; parse title/variant, category, seats, useful load,
fuel, cruise, min-runway where present; **curated fallback table** where manifests are
incomplete. Build `AircraftType` records with alias lists (reconcile the live
`"PC-12NGX Cargo - Empty"` title as the first real test case).

## Airport database (brief §3.6)

Bundle an OurAirports snapshot (`airports.csv`, `runways.csv`); import into SQLite on first
run; in-app HTTP update later. **Never** require the user to place a file.

## Economy slice (server-ready)

- `IJobSource` generates Cargo jobs at an airport (distance × weight → reward/xp).
- `IPriceProvider` exists from day one (local impl now; a server could supply it later).
- **Settlement**: base reward × modifiers (landing quality, penalties) → itemised
  `payoutBreakdown` → `LedgerEntry` writes → cash/xp update. Every figure traceable to the
  ledger (acceptance #3); no hidden multipliers (brief §3.5).

## Minimal UI

Job board (one airport) · live flight HUD (WebSocket telemetry + current phase) ·
settlement screen (itemised breakdown, XP, new balance) · flight log + ledger. First-run:
pick home airport, grant starting cash (a ledger entry), detect aircraft — tuned toward the
10-minute-to-first-flight funnel.

## Build order (each step independently verifiable)

1. **1a** Core + EF Core + SQLite migrations; `LedgerEntry` + `Pilot`; seed a pilot with starting cash (via ledger). Unit-tested.
2. **1b** OurAirports import → airport queries.
3. **1c** Installed-aircraft scanner → `AircraftType` roster; reconcile the live PoC title.
4. **1d** Cargo job generation at the home airport.
5. **1e** Flight-tracker state machine over `ISimTelemetrySource` (fake profiles first, then live) → a `Flight` with touchdown fpm + events. Extend telemetry SimVars here.
6. **1f** Settlement + itemised payout + ledger writes.
7. **1g** Host (REST + WS) + minimal React UI.
8. **1h** End-to-end: accept a Cargo job, fly it in MSFS, land, get paid, see the log. **Ship.**

## Testing

Economy/settlement → deterministic unit tests. Flight tracker → driven by scripted
`FakeTelemetrySource` profiles (touchdown at a known fpm → expected score). Scanner →
fixture `aircraft.cfg` files. CI extends to build Core/Host and run the new tests.

## Risks / open questions

- **SimVar coverage** for scoring signals (over-G, flap/gear limits) — verify early; extend additively.
- **aircraft.cfg variance** across MSFS 2024 — curated fallback.
- **Disk space:** only ~4 GB free on `C:` — bundling data + NuGet caches + MSFS is tight. Worth clearing headroom before Phase 1.
- **UI scope creep** — keep P1 UI minimal; QoL is threaded through, not a separate phase.

## Explicitly deferred

Market / buy / rent, all job types, rank / qualifications / check flights → **Phase 2**.
Staff / autonomy / offline → **Phase 3**. Bases / routes / trade / loans / P&L, and the
shared-world ADR → **Phase 4**.
