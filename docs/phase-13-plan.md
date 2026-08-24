# Phase 13 — "The Honest Sim" · master plan

Date opened: 2026-08-24. Source: a 30-item feedback batch from the user, grounded against the
current code by two research passes (flight/sim bridge + economy/statistics). This doc is the
execution source of truth; the visual overview is the companion Artifact.

Governing laws still apply: **L8** (certs gate categories, not fun), **L9** (Fun Dial — reward
mastery, coach mistakes, bill only real damage), **L10** (read the sim, default every new SimVar so
zero = old behaviour), **L11** (deepen don't duplicate — one wallet/ledger/read-model), **L12**
(operating rep converges to crew skill). Clean-room throughout; additive migrations only.

Legend — **P0** correctness/bug (ship first), **P1** core system, **P2** depth/polish.
Effort — S (hours), M (a day), L (multi-day), XL (a wave).

---

## WAVE 0 — Fast, high-signal wins (ship first)

- **[P0·S] Concorde.** Add DC Designs Concorde to `DefaultFleetCatalog.Aircraft2024` (ICAO `CONC`,
  Heavy, ~100 seats, long runway ~12000 ft) + `IcaoKeys`. Market price **$500,000,000**, **not
  rentable, not leasable**. Needs a per-aircraft "not rentable/leasable" flag (see Wave C) — Concorde
  is the first user of it. Curated Wikimedia image.
- **[P0·S] Tail-vs-name display.** Many spots show the random tail (`CS-xxxxx`) where the aircraft
  NAME belongs. Fix the selectors/tables to lead with the type name, tail secondary:
  App.tsx dispatch/ops/route/standing selectors (`1810, 3402, 3423, 3441, 3556`), and any `<option>`
  that is `{f.tail}` only. Pattern: `{f.name} · {f.tail}`.
- **[P0·S] "Waiting for simulator" spam.** `SimConnectTelemetrySource.PumpLoop` flips
  Connecting↔Disconnected every 5 s while MSFS is closed; `prevLink` guard passes each cycle →
  the flight log appends two lines every 5 s (`App.tsx:2292-2296`). Fix: retry silently in the
  background; log a connection line **once** on genuine state change (not on reconnect churn), and
  keep the readiness strip's static "Waiting for simulator" label only.
- **[P0·S] Phase-log spam.** Client logs `Phase — {p}` on EVERY phase-string change with no min-dwell
  (`App.tsx:2301-2304`); thresholds wobble near vs=±200 / GS=3 / AGL=1000. Fix: drop the
  climb/cruise/descent chatter entirely; keep only meaningful, debounced beats — Airborne, Top of
  climb, Descent, Approach, Touchdown, Secured — each with a minimum dwell so wobble can't re-fire.
- **[P1·S] Curated image reconcile.** The 20 entries in `CuratedAircraftImages.ByIcao` are overrides.
  Per the user, most sent images were NOT improvements — keep overrides ONLY for aircraft that had a
  bad/missing default (DR400 had none). ACTION: confirm the keep-list with the user, then trim the
  rest so they fall back to the default/scanned thumbnail. Add Concorde's image.
- **[P1·M] Hide jobs the plane/pilot can't do.** Jobs tab shows jobs the selected aircraft cannot
  perform (pax > seats, weight > useful load), so activating them dead-ends. Fix: when an aircraft is
  selected, filter or clearly grey-out+badge the impossible jobs (reuse `payloadFit`, `App.tsx:2342`).
- **[P1·M] Cancel a job.** Add cancel on an accepted/assigned job; it releases the job and docks a
  **tiny** client-reputation amount (new small `ClientLoyaltyCancelMilli`, e.g. −400 vs −6000 for a
  failed flight). Endpoint + Jobs/Ops UI.
- **[P2·M] More market filters.** Aircraft market: filters for category, capability (buy/rent/lease),
  seats band, payload band, price band, range/cruise, distance-to-you. Extend the existing search.

---

## WORKSTREAM A — Flight Fidelity & the Sim Bridge  (P0/P1, XL)

The sim bridge today has **no notion of the sim's loaded state** — pax, cargo, pause, and even which
aircraft is loaded are only soft-matched by title. The only hard gates are arrival geofence and
land-then-secure. This workstream makes the bridge honest.

1. **[P0·L] Real load check (kills the "1000 Passengers" bug). DECISION LOCKED: warn, don't block —
   pay ~15% less if underloaded.** The "sim has …" readout at `App.tsx:2395` interpolates `tele.title`
   (the aircraft TITLE string) — the sim's real pax/cargo is NEVER read. Two parts:
   - **Display bug:** stop printing the title as if it were passengers; show the JOB's pax/cargo and
     the aircraft's actual **TOTAL WEIGHT** (already read, `SimConnectTelemetrySource.cs:255-258`).
   - **Warn + reward cut (L9/L10):** compare the sim's live `TOTAL WEIGHT` against empty-weight +
     expected job payload (±generous tolerance). If the player didn't load the pax/cargo in MSFS,
     **never block** — show a warning and apply a settlement reward reduction (new
     `UnderloadedPayFactor`, ~0.85 = 15% less; logical, could scale with how short the load is). Zero
     reading = old behaviour (no penalty). This is the honest "you didn't really carry it" fee.
2. **[P0·M] Auto-start on takeoff.** Today a scored session starts only on manual `/api/flight/begin`.
   Reframe: selecting job+aircraft **arms** the flight; the timed/scored session officially begins on
   detected takeoff roll/liftoff (`FlightTracker` already auto-detects liftoff within an armed
   session, `FlightTracker.cs:193-212`). Remove the "press Begin to start flying" friction; Begin
   becomes "Arm flight."
3. **[P0·M] Finish sequence — land → brake → engine off → reward.** `IsSecured`
   (`FlightTracker.cs:282`) currently completes on `ParkingBrakeSet` **OR** `!EngineRunning` **OR**
   never-saw-engine. Tighten (L9, warn-first): require arrival at the right field (geofence already
   exists) **then** parking brake set **AND** engine off. Surface a live 3-tick checklist in the
   Flight tab so the player sees exactly what's left before the reward lands.
4. **[P1·M] Pause detection.** No pause handling exists. Subscribe to the SimConnect "Pause"
   system event; while paused, freeze the tracker clock and the trail (don't accrue time, don't draw
   a straight segment across the gap). Resume cleanly.
5. **[P1·M] Fly-line fix.** The trail (`App.tsx:2157-2172`) starts empty at arm time and only
   accumulates from the first post-arm frame, so arming mid-flight begins the line there and pause
   gaps draw a straight chord. Fix with pause handling (above) + seed the trail from the departure
   fix; bridge gaps as dashed, not solid.
6. **[P1·M] Plane-icon jitter.** Heading = bearing between consecutive frames, recomputed every frame
   with no smoothing (`App.tsx:2167-2169`); jittery early coords spin the icon "in all directions."
   Fix: only update heading when moved > a min distance; suppress the icon entirely until telemetry is
   genuinely live (Connected && moving); never plot synthetic drift.
7. **[P1·M] The 400 on Begin.** No gate compares the actual sim aircraft — the 400 is the location or
   rating gate (`CallsignWebApp.cs:1739-1791`). Fixes: clearer messages; the capability pre-filter
   (Wave 0) so you never arm an impossible job; and reverse-ferry (Workstream D) so "you're at X, job
   departs Y" is a one-click fix, not a dead-end.

---

## WORKSTREAM B — Economy Truth  (P1, L)

1. **[P1·M] Distance-led rewards.** Today pay is NOT distance-led: cargo weight ($1/lb up to $3000)
   and pax flat ($200/seat) dominate short legs. Retune so **distance-nm leads, then payload/pax**:
   raise `CargoPerNmCents` & `PaxPerPaxNmCents`, cut `CargoPerLbCents` & `PaxPerPaxCents`
   (`EconomyConfig.cs:15-17, 41-45`). XP is already distance-led (`5 + 0.1·nm`) — leave it. Keep the
   money-pump invariant (weather+rep lifts ≤ 2×spread) intact. Update the reward-formula tests.
2. **[P1·M] Forgiving engine/hull wear (L9).** Engine wear = hours·400 + `EngineDamagePctAccrued·1500`
   milli/pct (`SettlementService.cs:210`); ~67% accrued sim damage zeroes the engine in one leg.
   Soften: cap per-leg engine/hull loss, reduce the abuse multiplier, and keep the 3-tier Fun Dial
   (coach-no-cost → forfeit-bonus → real-damage-warned-first). One rough leg should never total an
   engine.
3. **[P1·M] Damage-scaled, separable servicing.** `MaintenanceQuoteCents` is flat ($500 + $200/hr
   since service) and `MaintainAsync` restores hull+engine **together** (`AircraftDealerService.cs:
   267-269, 307-308`). Change: price each service by how worn that component is (a 40%-engine costs
   more than a 90%-engine), and let the player service **hull, engine, or both**. Two quote functions
   + a component arg on the endpoint + Hangar UI (three buttons).
4. **[P1·M] Rank/rep-scaled borrow cap.** No cap exists — a Trainee can borrow up to $50M
   (`LoanCatalog.cs`, `LoanService.TakeAsync`). Add a max-principal ceiling that scales with rank &
   reputation (Trainee ~$50k → Flag Carrier large). Gate `TakeAsync`/`TierFor` on `pilot.Rank`+rep.
   Optionally a soft negative-cash floor at reconcile.
5. **[P1·M] Hired-pilot legs update the client (reduced).** Autonomous legs (`OperationsService.
   ReconcileAsync`) currently write NO client loyalty and NO pilot XP — only operating rep. Add a
   **reduced** `ClientLoyaltyDeltaMilli` and a **reduced reward factor** on crew-flown legs across the
   three reconcile loops (standing orders `:525-570`, dispatch `:623-630`, routes `:~789`). Logic:
   letting a hired pilot fly still pleases the client, but less than flying yourself (new
   `CrewLegClientFactor` < 1, `CrewLegPayFactor` < 1). Keeps L12 (operating rep unaffected).
6. **[P1·L] Whole stats/data-flow audit.** Findings to fix: (a) `FlightTotals.totalXp`,
   `totalFuelLbs`, `bestTouchdownFpm`, `bestPayoutCents`, `longestLegNm` are computed but never shown
   — surface them in the logbook; (b) dashboard/logbook Reputation & XP reflect only player-flown
   legs, misleading for autonomous-heavy companies — label them "pilot" vs show a company view;
   (c) `SmoothLandings` uses raw `TouchdownFpm`, not the scored worst-3 — align it. Sweep every stat:
   is it computed → sent → shown → correct.

---

## WORKSTREAM C — The Market & Aircraft  (P1/P2, M)

1. **[P1·M] Rentability curation.** Rentals currently offer ALL career types
   (`GetRentalOffersAsync` uses `IsCareerAircraft` only). Add a per-type `Rentable` decision: small/GA
   & workhorse types rentable; halo/heavy/bizjet/warbird types buy-only. Data-driven flag on the
   catalog. Concorde uses the same flag (not rentable/leasable).
2. **[P1·M] Remove lease completely. DECISION LOCKED.** The market becomes **Buy + Rent** only
   (rent = short, by-the-hour, fly-by-hand — intuitive). Retire the lease offers/UI (lease buttons in
   the unified market, the Leasable badge, `leaseByType`, lease sections, lease agreement management).
   Honour any existing lease agreements until returned/bought out; just stop offering new ones. Remove
   the now-dead lease config once no agreements remain, or leave it inert. Drop the `.cap.lease` badge.
3. **[P1·S] Market location behaviour (answer + option).** Already: the aircraft SET is anchored to
   your **home** region (so stock doesn't teleport when you fly), and the DISTANCE shown updates from
   where you are now. Optional add: also surface a few offers near your **current** field as you roam.
4. **[P2·S] Curated image reconcile** — see Wave 0.
5. **[P2·M] More market filters** — see Wave 0.

---

## WORKSTREAM D — Crew, Jobs & Automation  (P1, L)

1. **[P1·M] Auto-end hired-pilot jobs + notify.** Today the user must click "Recall" on a dispatched
   leg. Change: when a crew leg completes in reconcile, it ends **automatically** and raises a
   notification/toast ("{pilot} completed {origin}→{dest}, +{pay}"). Recall stays only for pulling a
   leg out of the air EARLY. Wire the reconcile digest → a user-facing notification feed.
2. **[P1·M] Per-plane pilot assignment.** In the Hangar, each tail gets an "assigned pilot" — hired
   pilots get their own aircraft; the user picks their own. Feeds the Fly-tab person selector and the
   dispatch defaults. New nullable `AircraftInstance.AssignedStaffId` (additive migration).
3. **[P1·M] Fly-tab person selector.** At the top of the Fly tab, pick **yourself or a crew member**
   to fly a specific job as that person. Flying as a crew member routes through the autonomous path
   (crew-leg factors); flying as yourself is the scored player path.
4. **[P1·M] Reverse-ferry (pay to reach the aircraft).** Today you can only pay to bring the aircraft
   to you (`RelocateAsync`). Add the inverse: pay to reposition **the pilot** to the aircraft's field
   (or to a job origin). Symmetric fee (distance-based). Resolves most 400-on-begin dead-ends.
5. **[P1·M] Job multi-select rules.** On the Fly tab / standing jobs: allow selecting **one** job if
   destinations differ; allow **two** jobs only if they share a destination **and** together fit the
   chosen aircraft's seats/useful-load. Enforce in UI + validate server-side at begin.
6. **[P1·M] Jobs-tab map: here-dot + route line.** Add a dot for where the player/selected crew member
   currently is (not just the destination), and on clicking a job draw a line from the current
   position to that job's destination.
7. **[P1·S] Hide/flag undoable jobs** — see Wave 0.
8. **[P1·M] Cancel a job with rep hit** — see Wave 0.

---

## WORKSTREAM E — The Airline Long-Game  (P2, XL · brainstorm → phased build)

**Problem:** having an "Airline" from day one feels unrealistic. Founding and running an airline
should come **much later**, be **earned**, and be **big, special, and genuinely complex**.

**Reframe (proposed):** the early game is an **Operator / Company** — you, a couple of planes, jobs
and charters. "Airline" becomes a **late incorporation milestone**, not the starting identity. This
DEEPENS the identity we already built (L11) rather than adding a new one: the operating-reputation
flywheel stays the core loop, but the **Airline tab, branding, and scheduled network unlock only when
earned**. (Today the Airline tab is present from the start — lock it.)

**The gate (must clear ALL):** sit at ≥ Regional on the `CareerLadder`, hold an **AOC**, own ≥ N
airliners (≥ M seats), operate ≥ K bases with a designated hub, sustain operating reputation ≥ X for
a period, and net worth ≥ Y. Only then can you **Incorporate an Airline**.

**Incorporation flow (a real, expensive, multi-step campaign):** choose a name + IATA/ICAO code +
livery/brand → file initial route authority → designate a primary hub → raise founding capital
(large capex + standing overhead) → pass an operational-readiness review. Ceremony matters: a
dedicated "Airline HQ" view opens on completion.

**Depth mechanics (build in waves after incorporation):**
- Scheduled **network planning** (routes, frequencies, seasonal timetable) — extends 11f scheduled
  service.
- **Fleet assignment** & utilisation; **crew rosters** with duty/rest limits.
- **Slots & gates** at congested hubs; **yield/ticket pricing** with load factors that ride operating
  reputation (the flywheel payoff).
- **Competition** & market share; **alliances/codeshare**; **disruption management** (weather
  cancellations already exist, 8f).
- **Board / investors / quarterly reports / share price**; expansion decisions.

**First slice to build:** lock the Airline tab behind the gate + ship the Incorporation flow +
the HQ shell. Everything else layers on. This is a wave of its own — brainstorm to converge on the
gate thresholds and the incorporation cost before building.

---

## WORKSTREAM F — UX & Polish  (P2, L)

1. **[P2·L] Bases tab redesign.** Today: hero-stats strip + satellite map + a facility table
   (`App.tsx:3664+`). Rework to base **cards** — each base a panel with its map pin, facility ladder
   (maintenance / fuel farm / hub) shown as visual levels with next-upgrade ROI, daily upkeep, the
   base manager, and the jobs/routes it feeds. Better-looking, scannable, less table.
2. **[P2·L] Staff tab + automation research.** Full lap: hiring, wages, skill, the Manager role,
   dispatch, and how it all feeds reconcile. Deliverable: a clearer Staff tab (who's flying what,
   who's idle, who's assigned to which tail — ties to per-plane assignment) + documented automation
   model. Research first, then redesign.
3. **[P2·M] Icons everywhere (tasteful).** Add iconography where it aids scanning without clutter —
   the Trade/commodity market first (per-good icons), then nav, status chips, facility types, mission
   types. Keep it restrained (avoid emoji-as-marker).
4. **[P2·M] Many more achievements.** Only 18 today (`AchievementCatalog.cs`). Add a broad set across
   distance/hours milestones, aircraft-type firsts, landing quality, economy milestones, network
   growth, weather ops, and the new airline arc.

---

## ANSWERED QUESTIONS

- **Does the market follow where I am / where I flew?** The DISTANCE to each aircraft updates from your
  current position as you fly. The SET of aircraft is anchored to your HOME region on purpose (so a
  plane for sale doesn't teleport when you reposition). Optional future add: also show a few offers
  near your current field (Workstream C.3).
- **Can we load objects/lines into MSFS itself?** Partly, and it matters for your future plans:
  - **Yes** — via SimConnect you can inject **AI objects** (aircraft, ground vehicles, static
    SimObjects) at lat/lon, and you can push a **flight plan / GPS route** so the sim's own map/GPS
    draws the magenta line to your destination.
  - **Not directly** — arbitrary freehand 3D lines/shapes drawn into the world need an in-sim
    **WASM gauge or scenery add-on** (a bigger build), not plain SimConnect. Recommendation: start
    with GPS-route injection + AI objects (cheap, high value), treat custom world-drawing as its own
    R&D spike.

---

## DECISIONS — RESOLVED (2026-08-24)

1. **Lease:** ✅ **REMOVE completely.** Market = Buy + Rent only. Honour existing agreements.
2. **Load gate:** ✅ **Warn only, no block** — apply ~15% reward reduction if the sim aircraft is
   underloaded (`UnderloadedPayFactor` ≈ 0.85, may scale with shortfall). L9-friendly.
3. **Curated images:** ✅ Reconcile `CuratedAircraftImages.ByIcao` to EXACTLY the list the user sent
   in the prior session — remove any override not on that list, add/replace the ones on it (DR400 etc.
   had no/bad picture). When implementing, recover the exact URL list from the prior transcript.
4. **Airline reframe:** ✅ **YES.** Early game = a small "Operator"; the "Airline" identity
   (name/livery/scheduled network/HQ) unlocks only after real growth. Build Workstream E on this
   footing. Exact gate thresholds + incorporation cost still to brainstorm when we reach that wave.

---

## SUGGESTED SEQUENCE

1. **Wave 0** (fast wins, mostly P0) — Concorde, name display, log spam, phase spam, job capability
   filter, cancel, filters.
2. **Workstream A** (flight fidelity) — the biggest correctness cluster; do load gate, finish
   sequence, auto-start, pause/fly-line/icon together.
3. **Workstream B** (economy truth) — reward retune, wear/service, borrow cap, hired-pilot client,
   stats audit.
4. **Workstream D** (crew/jobs/automation) — auto-end, per-plane pilot, person selector,
   reverse-ferry, multi-select, map.
5. **Workstream C** (market) — rentability, lease decision, filters, images.
6. **Workstream F** (UX polish) — bases, staff, icons, achievements.
7. **Workstream E** (airline long-game) — its own wave, after a brainstorm on thresholds/costs.
