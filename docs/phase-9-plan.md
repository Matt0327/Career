# Phase 9 — The Real Sky

## 0. Why this document exists

Phase 7 wired *consequence* into already-recorded state; Phase 8 gave the world *weather, time, an economy, clients and certificates*. Phase 9 deepens the **sim-integration layer**: read what the simulator actually knows — real weather and far richer telemetry — and make it economically real, **without turning the game into a nag-machine**.

Built from a three-stream research pass (competitors, MSFS 2024's native career, and the SimConnect SDK — see `Callsign Flight Plan`). The strategic thesis: Callsign's **un-gameable telemetry scoring** and its **weather → market coupling** are edges nobody else holds; Phase 9 compounds them while MSFS 2024's own career mode stays buggy, grindy, and locked to default aircraft.

## 1. The design laws (extending Phase 7's five and Phase 8's three)

Carry forward L1–L8. Phase 9 adds two, and **L9 is the one that governs the whole phase** — every new mechanic below is spec'd against it:

**L9 — The Fun Dial. Reward mastery, coach mistakes, bill only real damage.**
It still has to be *fun*. The #1 thing that sank the native career in reviews was punishment ("fifteen ways to accidentally go bankrupt"). Every deviation gets the **lightest response that still teaches**, on a three-tier ladder:

1. **Coach it (no cost).** A little too much bank, a firm-but-safe touchdown, a small drift off the magenta line → a friendly nudge in the **live flight log**. Purely informational; zero economic effect.
2. **Forfeit the *bonus* (base pay stands).** Sub-par-but-safe flying just earns no *excellence* reward — it never goes negative. (This is already how the 7c scoring lever works: a great landing earns a bonus, a mediocre one earns nothing, base pay untouched.)
3. **Real consequence — and only for genuine harm, warned first (L4).** A slam that damages the gear, *sustained* over-temp abuse, a gross overload, ignoring *severe* icing. Never a one-off minor exceedance; anything irreversible gets the warning turn before it executes.

The baseline is always "you're fine": **skill ADDS upside; only genuine damage SUBTRACTS, and you always see it coming.** A one-off over-bank is a nudge, not a *straf*.

**L10 — Read the sim, degrade gracefully.** Every new SimConnect signal is *optional*: defaulted so a missing/zero var — a complex third-party aircraft that doesn't publish it — leaves behavior exactly as before (the same isolation trick that kept Phase 7/8 test-churn at zero). Always request the **explicit unit** (the SDK defaults are traps: engine temps in Rankine, brake temps in Kelvin, oil pressure in psf, nav errors in radians, GPS distances in metres), and mind "percent" vs "percent-over-100" (0–100 vs 0–1).

## 2. The integration spine

Two seams carry Phase 9, both already built:

- **The flight-event stream (Phase 7a)** is the home of every tier-1 *coaching* nudge. `FlightTracker` already emits scored exceedance events; Phase 9 adds an **unscored `Coaching` severity** below the violation thresholds. These stream live during the leg (ws `type=event`), persist on the `Flight`, and become the raw material for the Phase-10 post-flight debrief.
- **`WorldOracle` (Phase 8)** is the weather seam. Live weather (9b) becomes an `IWeatherSource` that `WorldOracle` consults, with the synthetic model as the offline fallback — so nothing downstream (8a scoring, 8f market) changes shape.

## 3. The systems

### 9a. The Fun Dial + in-flight coaching  *(build first — the foundation)*
The response ladder, in code. A new **unscored `Coaching` event class** the tracker emits for *minor* deviations that sit **below** the existing violation thresholds — a bank in the "a touch steep" band, a firm-but-safe sink rate, a small overspeed margin, a steep-ish approach. Zero score effect, zero economic effect; it streams to the live log and seeds the 10a debrief. This establishes the tier-1 rung that **every later consequence mechanic warns through** — so no Phase-9 mechanic can ship as a punishment.

### 9b. Live weather → the market  *(the flagship — highest strategic value)*
Real METAR/TAF + winds-aloft as an `IWeatherSource` behind `WorldOracle`, with the synthetic model as fallback (offline / no-network). Feeds the *existing* 8a scoring and 8f weather→market coupling — a hook rivals can't cheaply copy because they have no market for weather to feed. Ships behind a config toggle, cached, and never blocks a flight if the feed is down (L10).

### 9c. Authoritative landing grade
Read the sim's own touchdown SimVars (`PLANE TOUCHDOWN NORMAL/LATERAL/PITCH/BANK VELOCITY/DEGREES`, `MAX G FORCE`) instead of frame-sampling vertical speed. Kills the "missed the peak frame" bug and adds side-load / de-crab grading. A direct upgrade to the moat; defaulted so manual/legacy records are unchanged (L10).

### 9d. Real fuel + weight-and-balance
True fuel weight and per-tank quantities; payload = total − empty − fuel; CG vs fwd/aft limits and overweight T/O & landing. **On the Fun Dial:** a small CG offset or a hair over is a *coaching nudge*; only a gross overload or way-out-of-envelope is a real handling/pay consequence — warned.

### 9e. Engine / airframe wear economy
Meter real abuse from raw stress SimVars (`GENERAL ENG DAMAGE PERCENT`, oil-leaked, TBO elapsed hours, over-temp on oil/CHT/EGT/ITT, over-torque, over-rev) into maintenance bills and resale hits — deeper and fairer than native's flat 25-hour timer, and clean-room independent of the sim's own wear model. **On the Fun Dial:** a brief exceedance coaches; *sustained* abuse accrues wear that eventually bills — never "−$X per exceedance".

### 9f. Guards & credibility
The low-lift batch that protects and legitimises the economy: a tighter anti-cheat net (`PositionChanged`, `WeatherModeChanged`, `Pause_EX1`, `AircraftLoaded` events), SimBrief/Navigraph OFP import, and aircraft leasing/rental (native career's most-requested missing feature).

*(Navigation/precision-approach scoring and icing/hazard ops carry into Phase 10 alongside the debrief & passenger-comfort moat-builders — see the Flight Plan.)*

## 4. Build order

Critical path **9a → 9c → 9b**: land the response ladder first (so every mechanic has a tier-1 rung), then the telemetry upgrades that are pure Core logic and testable here, then the live-weather external integration once the foundation is proven. 9d/9e/9f slot in after, each spec'd against L9. Live weather (9b) is the highest *strategic* value and should not wait long behind the foundation.

- **9a — Fun Dial + coaching.** Pure Core/tracker logic, fully testable. **Build first.**
- **9c — Authoritative landing grade.** Core tracker logic + Windows-only SimConnect field wiring (the struct/datadef additions compile only with the SimConnect binaries present — the same "untested-here" branch as 7b/8a-2).
- **9b — Live weather.** New `IWeatherSource`; the one slice with an external dependency — build behind a toggle with the synthetic fallback.
- **9d / 9e / 9f** — as above.

## 5. Consciously deferred

The Phase-10+ moat-builders (post-flight 3D debrief + coaching, passenger-comfort loop, nav/precision-approach scoring, icing ops) and the Phase-11/12 bets (airline-employment ladder, real airline endgame, asynchronous shared economy, live map, in-sim EFB). The hard constraint throughout: **Callsign is a PC add-on** — no SimConnect on Xbox/PS5/cloud; console reach only ever via an in-sim WASM/EFB panel (a separate stack).
