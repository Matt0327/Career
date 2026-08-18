# Phase 7 — Depth: wiring consequence into state we already record

**Status:** planned · **Opened:** 2026-08-18 · **Supersedes the premise of:** `docs/phase-6-plan.md`
**Companion:** the depth audit (artifact `332a7627`) that triggered this plan.

---

## 0. Why this document exists

Phase 6 rebuilt how the app *looks*. An adversarial audit of the real code then graded how deep it actually *is*, against NeoFly-4 = 100:

| Domain | Depth | Verdict |
|---|---:|---|
| Fleet & Aircraft | 44 | approaching |
| Missions & Economy | 42 | still-shallow |
| Crew, Bases & Progression | 33 | still-shallow |
| Flight, Logbook & Live Ops | 32 | still-shallow |
| **Overall** | **~38** | **still-shallow** |

Craft/look scored **~80 ("mostly-human")** on the same code. So the problem is **not** the surface. The problem, in one line the whole audit converged on:

> **Callsign has NeoFly's screens and ledger — but not its simulation. It records state, then does nothing with it.**

`skill%`, `FuelUsedLbs`, per-tail `HullConditionMilli`/`EngineConditionMilli`, touchdown fpm, telemetry — **all recorded today, all spent on nothing.** Phase 7 is the mechanics layer that *spends* them. It is deliberately **not** more screens.

This is a big plan. Read §1–§3 first — they are the constitution and the integration spine that every system obeys. §4 is the five systems. §5 is the build order. §6 is what we are consciously **not** doing yet.

---

## 1. The five design laws (non-negotiable)

Every mechanic in this plan is checked against these. If it breaks one, it is redesigned or cut.

### Law 1 — We score the sim; we never command it.
Callsign observes MSFS through SimConnect. It cannot make the engine quit, force a payload, or stop a bank. Therefore **every mechanic that claims to *change* the flight is theater; only mechanics *computed from* the flight are real and enforceable.** The audit caught three theater mechanics — we convert all of them:

| ❌ Theater (claims to change the flight) | ✅ Scored (computed from the flight) |
|---|---|
| "Overload debits climb performance" | Planned load vs **actual departure fuel** (`FuelQuantityLbs`) reconciled at settlement; pay `DeliveredFraction`, penalize the gap |
| "Engine fails mid-leg" (sim keeps purring) | **Post-flight discovered squawk** + raised dispatch-refusal probability *next* leg — an economic bet, never fake cockpit drama |
| "Passenger alarmed" as an injected event | Comfort **scored from recorded bank/G/VS** — the alarm is a *reading*, not a command |

If you cannot compute it from a `TelemetrySnapshot`, it does not belong in the sim loop.

### Law 2 — One real decision per system.
Depth is decisions, not data. Each system must create at least one genuine choice the player sweats:
- **Missions:** fill the hold for money or carry fuel margin for safety? Fly the Sensitive run like a limo or eat the damage?
- **Flight:** go around and lose time, or force an unstable approach and eat the score penalty?
- **Aircraft:** maintain now or run this worn tail one more leg? Tanker cheap fuel or uplift at the field?
- **Crew:** leave the tail idle 16h, hire a second greener pilot, or push your ace past the duty cap?
- **Bases:** expand here, open there, or sell a tail? Stock the fuel farm now or wait?

A mechanic that adds a number but no decision is cut.

### Law 3 — Hella realistic, but fun. Start lenient, then tighten.
We model real Part-135 practice (acceptance criteria, FDM exceedances, stabilized-approach, `Vref = 1.3·Vs0`, FAR 91.409 100h+annual, TBO, FTL duty caps, useful-load split, wholesale fuel/tankering). Where realism becomes spreadsheet tedium, we say so out loud with an explicit **realism dial** on each mechanic: `pure-real` / `real-simplified` / `gamified`. **Every balance dial ships lenient** — generous reserves, high fail-floors, low incident rates — and tightens only once the loop is proven not to become a chore or a soft-lock.

### Law 4 — Never reopen to disaster.
The app advances on real elapsed time through an unattended `ReconcileAsync`. Autonomous consequences (annuals coming due, a hull lost, a loan default, a base seizure) **must be warned in the reopen digest with a grace turn to react** — reconcile narrates risk *before* it executes damage. No player ever reopens to a fait accompli.

### Law 5 — The invariants that must survive every slice.
These are acceptance criteria on **every** PR, not afterthoughts:
1. **Additive, migration-guard-gated schema.** New columns are nullable/defaulted; new enum values appended; new tables only. `DatabaseBootstrapTests` (the migration guard) is the gate; a prior-version DB must open unchanged.
2. **Deterministic RNG seeded from persisted watermarks — never `Date.now()`/wall-clock.** Every per-trip/per-window roll seeds from `(entityId, ordinal, LastReconciledAt.Ticks)` so reconcile stays **idempotent** (run twice over a window → identical rows).
3. **All tunables behind versioned `EconomyConfig`, defaults as no-ops** so existing settled quotes and the current test suite hold. Frozen quotes are frozen: a retune never rewrites an accepted contract (mirror the existing frozen-quote guarantee; `MissionProfileVersion` does this for missions).
4. **Every money move is a dedupe-keyed `LedgerPosting`.** `Company.CashCents` stays the ledger sum. Each `PayoutLine` maps 1:1 to a posting.

---

## 2. The integration spine (read before any system)

The audit's single most important structural finding: **treated as five independent features, these designs collide and the ledger invariant breaks.** Four of them rewrite the same ~200-line `SettlementService.SettleAsync`, three independently invent payload-vs-fuel weight & balance, two each reinvent the Dispatcher role and a fuel-cost subsystem. So we do **not** build five peers. We build **one pipeline with single owners**, and the systems contribute stages to it.

### 2.1 The one Settlement Pipeline
`SettleAsync` becomes a fixed, ordered pipeline. Every system adds a *stage*, never a competing "primary multiplier":

```
settle(flight, assignment, tail, crew):
  1. COMPLETION GRADE   (Missions)      → Full | Partial | Failed   ─ gates whether any base pays
  2. QUALITY MULTIPLIER (Flight score)  → ×PerformancePct + integrity/comfort/coverage  ─ scales the base
  3. DELIVERED FRACTION (Aircraft W&B)  → ×(delivered / requested)   ─ scales for short-loaded legs
  4. ITEMIZED LINES     (Aircraft+Bases)→ − fuel − landing/handling fee − unscheduled repair + tips
  5. WEAR & XP & REP    (Flight+Crew)    → hull/engine wear, pilot XP, reputation from the same score
```

Each term is a `PayoutLine` → dedupe-keyed `LedgerPosting`, exactly as today. There is exactly **one** author of `SettleAsync`; the systems below specify what they *contribute* to a stage.

### 2.2 Single owners (kills the duplication the audit found)
| Shared concern | Sole owner | Everyone else… |
|---|---|---|
| **Telemetry substrate** (the ~18-field `TelemetrySnapshot`, FDR track) | **Flight Scoring (§4.2)** | reads it; nobody else expands it |
| **Weight & balance** (`UsefulLoadLbs`, payload+fuel budget) | **Aircraft Consequence (§4.3)** | consumes the budget; reconciled from telemetry, not promised pre-flight |
| **Fuel economy** (`FuelBurnLbsPerHour`, per-field price, per-tail `FuelOnBoardLbs`) | **Aircraft Consequence (§4.3)** owns per-tail fuel + the tanker decision | **Bases (§4.5)** fuel farm is the *wholesale source* D4.3 draws from — one `Fuel` ledger line, one price service |
| **Crew roles** (`StaffRole`, incl. `Dispatcher`, `Staff.BaseId`) | **Crew Depth (§4.4)** | **Bases (§4.5)** consumes the dispatcher, adds only the FBO-margin effect |
| **Shared constants** (`ReserveFrac`, `PaxWeightLbs`, `FuelBurnLbsPerHour`) | **`EconomyConfig`** — one home | no system re-declares them |

### 2.3 The dependency truth
Telemetry is the foundation, not a peer. Comfort (§4.1), wear (§4.2/§4.3), the skill roll (§4.4) and the fuel reconcile (§4.3) **all read fields the telemetry substrate adds.** Nothing downstream starts before §4.2's substrate lands. That is why the build order in §5 is a spine, not a fan-out.

---

## 3. The state we already waste (the highest-value, lowest-risk first move)

Before any XL build: **charge the fuel we already burn.** `Flight.FuelUsedLbs` is recorded on every leg today and costs **$0**. Pricing it is small, it bites every single leg, and it unlocks tankering. It is the cleanest proof of the whole thesis and it ships inside §5's 7e-fuel slice. Alongside it, the one true fastest win — making the fake event log real (§5, phase 7a) — needs no new data at all.

---

## 4. The five systems

Each system: the decision it creates, the real basis, the load-bearing mechanics (with realism dial), the data deltas, the formulas that matter, and its acceptance criteria. Code anchors are real files verified against the repo.

### 4.1 Mission Taxonomy & Completion Engine  — *"ten costumes" → ten real jobs*
**Effort: XL · closes:** mission types are one settlement rule in costumes; no per-type completion/failure; no multi-manifest; no flying-quality failure conditions; no contracts surface.

**The decision:** the payout stops being a number you already know and becomes a number you *earn in the air* — and, at accept, "fill the hold for revenue vs. carry fuel for margin."

**Real basis:** Part-135 categories are paid against genuinely different acceptance criteria — freight on intact delivery, courier on a clock, medevac on a hard time wall + smooth ride, survey on holding an altitude band, SAR on actually locating before recovery.

**Mechanics**
1. **`MissionProfile` completion engine** *(real-simplified)* — a static `MissionProfiles` table keyed by `MissionType`, parallel to `MissionCatalog`. Each profile is typed predicates evaluated against the completed `FlightRecord` at settlement, returning `MissionOutcome { Grade: Full|Partial|Failed, QualityMilli 0..100000, ConditionResults[], FailReasons[] }`. Cargo's profile is intentionally **lax** (integrity + landing only) so today's behavior is the preserved baseline; premium types add real constraints.
2. **Fragile-cargo integrity** *(real-simplified)* — assignment carries `IntegrityMilli` (start 100000); debited by rough-handling *observed* in the record: hard touchdown over the type's `SoftFloorFpm` (Cargo 400 / Sensitive 200 / Hazardous 150), taxi-overspeed, VS excursions. Base scales by integrity; below `FailFloor` → **Failed** (goods refused).
3. **VIP / Tourist comfort** *(gamified)* — scored from **recorded** bank/G/VS (needs §4.2's `BankDeg`,`GForce`): `Comfort ≥ 90000` pays a tip line; `< 60000` books a complaint (payout − and a negative reputation event); a single >45° bank is a logged "passenger alarmed" reading.
4. **Delivery clock** *(real-simplified, with the audit's fix)* — Express: `DeadlineAt = AcceptedAt + (RouteNm/CruiseKts)·1.6`, frozen at accept. **Medevac uses an absolute time wall** (patient-critical timer independent of distance — a real golden hour, not a distance scale), so a long leg genuinely blows it. Late → penalty; past the hard wall → Failed.
5. **Altitude-band** *(real-simplified)* — Survey/patrol require holding `[FloorFt, CeilFt]`; `CoverageMilli` = fraction of airborne time in band; below `MinCoverage` → survey unusable.
6. **SAR locate-before-recover** *(real-simplified)* — must overfly a frozen `SearchLat/Lon` within `LocateRadiusNm` before landing, else Failed even on a clean landing.
7. **Multi-manifest load planning** *(real-simplified, with fix)* — `ManifestGroupId` groups compatible accepted jobs into one tail; **settlement is partial/out-of-order-tolerant** (settle whichever drops were actually reached, abandon the rest with a rep ding — never an unclosable manifest).
8. **Payload-vs-fuel at accept** *(real-simplified)* — a `LoadPlanDto` preview: feasible if `payload + requiredFuel ≤ UsefulLoadLbs`. **This is a preview; the binding W&B is §4.3's settlement reconcile against actual fuel** (Law 1). Overload to `OverloadCapPct` (110%) allowed but flagged.
9. **Graded settlement** *(real-simplified)* — `SettleAsync` stage 1: Full pays as today; Partial scales base by quality with each deduction an itemized `PayoutLine`; Failed pays no base but still closes the assignment with a `Flight` row + `ReputationEvent`.
10. **Contracts surface** *(real-simplified)* — a Contracts screen over the extended `/api/assignments`: live deadline countdowns, capability/feasibility badges, manifest build/break, launch.

**Data:** `JobAssignment` += `DeadlineAt?`, `IntegrityMilli`, `ManifestGroupId?`, `ManifestSeq`, `AltFloorFt?`/`AltCeilFt?`, `SearchLat?`/`SearchLon?`, `MissionProfileVersion`, `Overloaded`. `Flight` += `MissionOutcomeJson?`, `OutcomeGrade`. `MissionType` += `Survey`,`Medevac`,`Ferry`,`Humanitarian` — **reconciled against existing `Emergency`/`SAR` so we don't ship two enum values players read as the same job.** New: `MissionProfile` (static), `MissionOutcome` (record).

**Anchors:** `SettlementService.cs`, `MissionCatalog.cs`, `MissionType.cs`, `JobAssignment.cs`, `JobAssignmentService.cs`, `FlightRecord.cs`, `FlightTracker.cs`, `Dtos.cs`, `CallsignWebApp.cs`.

**Depends on:** §4.2 (reads bank/G/altitude/track), §4.3 (W&B reconcile), the pipeline (§2.1).

---

### 4.2 Flight Scoring, Telemetry Substrate & the Real Event Channel  — *the foundation*
**Effort: XL (but its 7a slice is S) · closes:** 8-SimVar substrate; single gameable fpm; no violation engine; no FDR; the cosmetic event log; score not tied to pay/XP/rep; weak preflight; binary diversion; no anti-cheat.

**The decision:** go around and lose the clock, or press an unstable approach and eat the score? And: the cockpit becomes *honest* — a slam reads as a slam, forever, in the logbook.

**Real basis:** FDM/QAR exceedance monitoring, stabilized-approach criteria, `Vref = 1.3·Vs0`.

**Mechanics**
1. **Expanded substrate** *(pure-real)* — grow `TelemetrySnapshot` from ~8 to ~18 SimVars: `GForce`, pitch/`BankDeg`, true heading, AGL, flaps %, gear %, combustion, stall/overspeed warning, **sim-rate**, **slew-active**, per-tank fuel. This is the one and only telemetry expansion (§2.2).
2. **Persisted real `FlightEvent`s — the emotional fix** *(pure-real)* — the events `FlightTracker` **already builds** are written to a new `FlightEventRecord` table in the *same settlement transaction* as the `Flight` row. The Flight tab's live log switches from client-side fabrication to the WebSocket stream of **real** tracker events; the logbook detail renders the persisted timeline. **This is 7a and ships first (§5).**
3. **Worst-of-three landing grade** *(real-simplified)* — `TouchdownFpmWorst3 = min(last 3 airborne VS samples)` (un-cherry-pickable) + peak G + bank at contact; `LandingScore = MIN(fpmScore, gScore, bankScore)` — only as good as your worst axis.
4. **Stabilized-approach gate** *(real-simplified)* — below 1000 ft AGL (500 for light singles) each sample must satisfy IAS ∈ `[Vref-5, Vref+20]`, ≤1000 fpm descent, ≤10° bank, configured; `ApproachScore` = % of gate samples passing; a hard "Unstable Approach" event otherwise.
5. **Violation/exceedance engine** *(real-simplified)* — per-snapshot evaluator emits a scored `FlightEvent` on first breach: overspeed (`>Vne`, or `>Vfe` with flaps), over-bank, over-G, airborne stall warning, unstable approach, gear-up landing, taxi overspeed. Each carries `PenaltyPoints` + `CashPenaltyCents`.
6. **Composite score → consequence** *(gamified)* — `OverallScore = 0.55·Landing + 0.30·Approach + 0.15·Enroute`. Drives `PerformancePct` pay multiplier (**+0.15 … −0.30**, replacing raw-fpm as the *stage-2* lever), XP multiplier (0.5–1.25×), reputation delta, and continuous hull/engine wear. **This is 7c and is the pivot that makes every later system pay off.**
7. **FDR track + replay** *(real-simplified)* — downsample to a compact polyline → `Flight.TrackJson`; the logbook draws the real ground track + vertical profile with events pinned. *(Lower priority — polish after 7c.)*
8. **Pre-flight validation** *(real-simplified)* — `POST /api/flight/preflight` returns pass/warn/**block**: rating held, condition ≥ min, positioned at origin, payload ≤ useful-load (block), fuel ≥ trip+45-min reserve (warn/block), runway length. *(Block requires explicit override.)*
9. **Graded diversion** *(real-simplified)* — replace the binary radius check: end near a real airport → partial `= base · ProgressFraction · DiversionFactor` (0.85 no-rep-hit if fuel was critical, else 0.60).
10. **Anti-cheat** *(gamified, with the audit's fix)* — `ScoreValid=false` on airborne slew, teleport jumps, fuel-up-in-flight, **or sim-rate >1 only inside the terminal/approach/landing window** (enroute time-compression on a 2-hour ferry stays legit).
11. **Wear from score** *(real-simplified)* — `hullWear` scales continuously with worst-3 fpm and touchdown G; gear-up/over-G inflicts a discrete damage hit that can open an insurance-claim path.

**Data:** `Flight` += `LandingScore?`,`ApproachScore?`,`OverallScore?`,`TouchdownG?`,`TouchdownFpmWorst3?`,`StabilizedApproach?`,`ViolationPoints?`,`ScoreValid`,`FuelReserveMinutesAtArrival?`,`DiversionIcao?`,`TrackJson?`. New `FlightEventRecord` (append-only, FK+index). `AircraftType` += `Vs0Kts?`,`VneKts?`,`VfeKts?`,`RetractableGear`.

**Formulas:** `Vref = 1.3·Vs0` (category fallback when null); banded fpm/G/bank scores; `PerformancePct` bands as above.

**Anchors:** `TelemetrySnapshot.cs`, `SimConnectTelemetrySource.*`, `FakeTelemetrySource.cs`, `FlightTracker.cs`, `FlightRecord.cs`, `Flight.cs`, new `FlightEventRecord.cs`, `AircraftType.cs`, `SettlementService.cs`, `CheckFlightService.cs`, `CallsignDbContext.cs`, `CallsignWebApp.cs`, `Dtos.cs`.

**Depends on:** nothing (it is the foundation). 7a is off the critical path — ship it regardless.

---

### 4.3 Aircraft Consequence: Condition, Failures, Maintenance & Fuel/Payload  — *the tail acquires weight*
**Effort: XL · closes:** condition grounds nothing; no failures; snap-to-100 maintenance; no legal inspections; no fuel/payload budget; no fuel management; no used market.

**The decision:** "can *this* airframe, with *this* fuel/payload split, legally and safely make *this* leg — and is it worth the risk?" Maintain-vs-fly; tanker-vs-refuel.

**Real basis:** FAR 91.409 100-hour + annual, TBO, MEL grounding, useful-load = MTOW − empty split between fuel and payload, tankering economics, hours/age depreciation.

**Mechanics** (all `real-simplified` unless noted)
1. **Airworthiness gate** — `AirworthinessService.IsAirworthy`: unairworthy (dispatch refused, with the specific reason) if `min(hull,engine) < AirworthyFloorMilli` (20%) **or** a 100h/annual is overdue.
2. **100-hour inspection** — `Last100hHoursWatermark`; due at +100 airframe hours; overdue → grounded; cleared by a shop line advancing the watermark.
3. **Annual inspection** — `LastAnnualAt`; due at +365 days. **Audit fix (Law 4):** advance-warn "annuals due in N days" in the reconcile digest and let one shop visit clear a fleet batch — reopening after a break must never be a wall of forced spend.
4. **Engine TBO & overhaul** — `OverhaulHoursWatermark` + `AircraftType.TboHours` (piston ~2000 / turboprop ~3600 / jet ~5000); past TBO steepens the failure roll; overhaul line restores engine + resets watermark.
5. **Condition-driven squawk** *(gamified → reframed per Law 1)* — one deterministic seeded roll at completion; **not** in-cockpit drama. A hit becomes a **post-flight discovered squawk** (`DeferredDefects++`, economic repair) or elevated **next-leg** refusal probability. `pFail` rises with engine deficit, overdue inspections, deferred defects, TBO overrun — **capped low and lenient at ship.**
6. **Incident consequence at settlement** — squawk/incident voids the landing bonus, posts a penalty + unscheduled repair line, small rep hit; sufficient trip-insurance tier converts a total loss to a deductible claim.
7. **Fuel/payload useful-load budget** — `BeginFlightRequest += FuelLbs`; enforce `payload + FuelLbs ≤ UsefulLoadLbs` and `FuelLbs ≥ fuelReq`. **Binding form (Law 1):** the *authoritative* check is the settlement reconcile of planned vs **actual** departure/burn fuel from telemetry — pay `DeliveredFraction`, penalize a planned-vs-actual gap. The pre-flight budget is guidance for hand-flown tails; the settlement reconcile is the enforcement.
8. **Fuel required = range + reserve** — `fuelReq = ceil(burn · dist/cruise · (1+ReserveFrac))`, `ReserveFrac ~0.25`, generous fallbacks so no type becomes undispatchable.
9. **Fuel uplift, per-field price & tankering** — per-tail `FuelOnBoardLbs`; at dispatch `uplift = max(0, FuelLbs − onboard)` debited `LedgerCategory.Fuel` at the field price (seeded swing like `MarketService`); post-flight carry `FuelOnBoardLbs' = max(0, FuelLbs − FuelUsedLbs)` → **tankering** becomes a real arbitrage.
10. **Planned-vs-actual reconcile** *(gamified)* — actual burn > available → fuel-mismanagement penalty + void bonus; efficient planning credited in the logbook.
11. **Used-aircraft market** — deterministic slate (type + year + hours + condition + depreciated price) from a seed+refresh window like `OperationsService.GenerateCandidates` (no new table); buying materializes an `AircraftInstance` carrying its hours/age/condition/watermarks.

**Data:** `AircraftInstance` += `Last100hHoursWatermark`,`LastAnnualAt?`,`OverhaulHoursWatermark`,`FuelOnBoardLbs`,`DeferredDefects`,`YearManufactured?`. `AircraftType` += `FuelBurnLbsPerHour?`,`EmptyWeightLbs?`,`TboHours?`. `Flight` += `FuelPlannedLbs`,`PayloadLbs`,`FuelUpliftCents`,`IncidentCode?`,`DeliveredFraction`. `JobAssignment` += `TripInsuranceTier`,`TripInsurancePremiumCents`. New `AirworthinessService`. **Curated specs are progressive enrichment with generous category fallbacks — never a gate** (honor the existing "§5.3 never fail a flight over identity").

**Anchors:** `AircraftInstance.cs`, `AircraftType.cs`, `CuratedAircraft.cs`, `AircraftDealerService.cs`, `AircraftPricing.cs`, `SettlementService.cs`, `OperationsService.cs`, `InsuranceService.cs`, `Settlement`/`FlightRecord`, new `AirworthinessService.cs`.

**Depends on:** §4.2 (actual fuel + telemetry), §4.1 (delivered fraction), the pipeline.

---

### 4.4 Crew Depth: Skill That Matters, Fatigue, Roles & Progression  — *a bench, not a cost line*
**Effort: XL · closes:** skill never used; no fatigue; no hired-pilot XP/quals; one role only; slate never rotates; pilots not bound to tail/base.

**The decision:** "This tail can legally fly ~8h/day on one pilot — leave it idle 16h, hire a second (cheaper, greener, riskier) pilot to run it round the clock, or push my ace past the fatigue line and gamble an incident?"

**Real basis:** flight-time/duty limitations + rest, type ratings & currency, crew rostering/basing, dispatchers/mechanics as distinct functions, experience-driven proficiency.

**Mechanics**
1. **Skill finally bites the autonomous roll** *(real-simplified)* — `ReconcileAsync` currently books `floor(elapsed/rt)` trips at full income, zero variance. Add a per-trip outcome roll seeded `(order.Id, tripOrdinal, LastReconciledAt.Ticks)`: `pIncident = BaseRate·(1 − EffectiveSkill/100000)^k`. Skill now changes income and wear — today it changes nothing.
2. **FTL duty cap** *(real-simplified)* — `MaxDutyHoursPerDay = 8`; cap bookable trips so rolling-24h duty ≤ cap, then a mandatory `MinRestHours = 10` window. One pilot ⇒ ~8 of 24h ⇒ to run a tail continuously you must **own crew depth.**
3. **Fatigue curve** *(real-simplified)* — `FatigueMilli += 6000/block-hr`, recovered by a completed rest; `EffectiveSkill` subtracts a fatigue penalty, so the last legal trip of the day rolls at markedly higher risk than the first.
4. **Incident tiers** *(gamified → audit fix)* — minor ~70% (wear + small dock) / diversion ~22% (partial + divert fee) / major ~8% (trip lost, heavy wear). **Major severity scales with player *choice* (how far past the FTL cap, how green the pilot), is hard-capped in frequency, and is gated behind already-low condition** — a consequence, not a slot machine — and always warned per Law 4.
5. **Type ratings** *(pure-real)* — reuse `QualClass`; a category→class map; `CreateStandingOrder`/`CreateRoute` reject an unrated crew with the reason.
6. **Staff XP & proficiency** *(real-simplified)* — staff gain XP per reconciled trip; `SkillMilli` drifts up with hours-on-category, capped by `Level` (0–5 via `RankTiers.ForXp`), which raises the wage and ceiling. Hired pilots become **appreciating assets.**
7. **Mechanic role** *(real-simplified)* — stationed at a base: −25% condition wear for based tails, auto-performs due maintenance at −40% shop price, posted through the ledger.
8. **Dispatcher role** *(real-simplified — Crew owns the enum; §4.5 consumes it)* — company-wide: +2 board slots biased to higher-reward legs, −15% landing/handling fees, tighter FTL utilization.
9. **Cabin crew** *(real-simplified → audit fix)* — **threshold raised to ~9–10 seats** (real GA carries 4–9 pax with no cabin crew; the FAA wall is ~19). Below that, cabin crew is an **optional service upsell** driving tips/reputation, not a hard lock; above it, a soft gate.
10. **Crew basing & relocation** *(real-simplified)* — `HomeBaseIcao`/`CurrentIcao`; an order is crewable only if `CurrentIcao == origin`; relocation costs like a ferry.
11. **Rotating candidate market** *(gamified)* — reseed `GenerateCandidates` with `hash(companyId, floor(now/24h))` so the slate rotates; all four roles; right-skewed skill so aces are rare; signing bonus via the ledger.
12. **Player-pilot proficiency** *(real-simplified)* — the hand-flying player's own pilot earns currency; a small bounded `ProfBonusPct` (≤4%) for current-and-rated, small penalty for lapsed — never dwarfs the landing score.

**Data:** `Staff` += basing/`Xp`/`Level`/`FatigueMilli`/duty-window fields/`BoundAircraftInstanceId?`. New `StaffQualification`, `CrewIncident`. `Flight` += `FlownByStaffId?`,`IncidentKind?`. `StaffRole` += `Dispatcher` (persisted as string, additive-safe).

**Anchors:** `Staff.cs`, `PilotQualification.cs`, `AircraftInstance.cs`, `StandingOrder.cs`/`Route.cs`, `OperationsService.cs`, `SettlementService.cs`, `CheckFlightService.cs`, `RankTiers.cs`, `InsuranceService.cs`, `CallsignDbContext.cs`.

**Depends on:** §4.2/§4.3 (score + wear it feeds off), the pipeline.

---

### 4.5 Bases / FBO & the Living Economy  — *the map becomes a business*  ⚠️ split required
**Effort: XL×5 — this is five systems in one trenchcoat; it MUST ship as independent slices (§5).** Closes: flat rent tiers; no facilities/capacity/upgrades; no FBO stock; no player-priced lines; market not coupled to supply/demand; loans/insurance shallow.

**The decision:** where you base, how you stock fuel, and which lines you price create a standing decision with money on it every leg.

**Real basis:** FBOs sell fuel/services and hold inventory; hub capacity limits; regional supply/demand drives rates; fixed-vs-variable cost; financing/insurance as ongoing cash-flow drivers.

**Mechanics** (each an independently shippable slice — order in §5)
1. **Fuel as a real per-flight cost** *(real-simplified)* — charge `Flight.FuelUsedLbs` (today $0) at settlement: from a stocked base fuel farm at weighted-avg cost, else field retail. **Highest-value interlock; ship first** (§3).
2. **Facility tiers** *(real-simplified)* — Hangar / Fuel Farm / FBO Office / Maintenance Shop, each level 0–3, a capex ledger debit via `UpgradeFacilityAsync` (idempotency-keyed).
3. **Hangar capacity = hard slot count** *(real-simplified)* — sum of capacity caps based tails; overflow pays a daily tie-down surcharge → "expand vs open vs sell."
4. **Fuel farm wholesale + FBO retail** *(real-simplified)* — buy fuel at wholesale into `FuelStockLbs` with weighted-avg cost (TradeService lot-blend math); FBO retails the spread as income.
5. **Commodity market coupled to supply/demand** *(gamified)* — keep `MarketService.Quote`'s pure hash as fair-value, then multiply by a stateless regional export/import bias **and** a `BaseMarketPressure` term that you move by trading (over-sell and you crash your own price), decaying back over time.
6. **Player-authored, player-priced contract lines** *(gamified)* — `ContractLine`: define a repeating leg and **set your rate** around fair value; fill probability rises with how generously you price it.
7. **Dispatchers assigned to a base** *(real-simplified — consumes §4.4's role)* — need an FBO Office; raise local board quality, FBO margin, fee reductions.
8. **Fixed-vs-variable made legible** *(pure-real)* — facilities/dispatchers add daily upkeep billed in reconcile, attributed per base via `LedgerPosting.BaseId`; `FinanceService` gains a per-base overhead-vs-income view (an idle base reads net-negative).
9. **Credit rating → loan terms + missed-payment cascade** *(real-simplified → Law 4)* — `Company.CreditRatingMilli` prices APR and ceiling; a missed payment posts a late fee + rating drop; **default/seizure only after a warned grace period — never on the first missed reconcile.**
10. **Insurance experience rating + deductible choice** *(real-simplified)* — choose deductible at underwriting (higher deductible → lower premium); claims raise subsequent premiums.
11. **Base maintenance shop** *(real-simplified)* — discounts maintenance at the tail's base; closes the wear loop from §4.2/§4.3.

**Data:** `Base` += facility levels + `FuelStockLbs`/`FuelStockAvgCostCents` + upkeep watermarks. New `BaseMarketPressure`, `ContractLine`. `Company` += `CreditRatingMilli`,`InsuranceExperienceMilli`. `Loan` += `MissedPayments`,`LastMissedAt?`. `InsurancePolicy` += `DeductibleMilli`,`ClaimsCount`. `Staff` += `BaseId?`. `LedgerCategory` += `FboRevenue`,`FacilityUpkeep`.

**Anchors:** `Base.cs`, `BaseService.cs`, `SettlementService.cs`, `OperationsService.cs`, `MarketService.cs`, `TradeService.cs`, `LedgerEntry.cs`, `Company.cs`, `Loan.cs`/`LoanCatalog`, `InsurancePolicy.cs`, `Staff.cs`, `FinanceService.cs`.

**Depends on:** §4.3 (fuel model) and §4.4 (dispatcher). **Ships last** — its fuel cost is a real economy nerf needing a dedicated reward-vs-fuel balance pass so it doesn't strangle early-game cash flow.

---

## 5. The build sequence

Telemetry → composite score → everything downstream. Two branches (missions, crew) parallelize off the spine. **Critical path: 7a → 7b → 7c → 7e → 7g.**

| Phase | System / slice | Effort | Depends | What the player *feels* ship-day |
|---|---|---|---|---|
| **7a** | **Real event channel** (§4.2 m2) — persist tracker `FlightEvent`s, stream the real ones, render in logbook | **S** | — | The in-flight story becomes **true** and survives into the logbook. Biggest immersion jump for the least code. **Do this first, regardless of the rest.** |
| **7b** | **Telemetry substrate + landing/approach grade** (§4.2 m1,3,4,5) | L | 7a | Every landing and approach is judged, visibly and consistently. A greaser ≠ a slam. |
| **7c** | **Composite score → consequence** (§4.2 m6,11) — pay/XP/rep/wear + anti-cheat | L | 7b | The grade **bites**: fly well, earn more and grow; fly badly, wear the airframe. The core loop. |
| **7d** | **Mission taxonomy & completion** (§4.1) — *branch, parallel with 7e* | XL | 7b, 7c | Jobs finally diverge: a VIP leg, a medevac clock and a survey band are genuinely different games. |
| **7e** | **Aircraft consequence** (§4.3) — *split:* (e1) fuel cost + fuel model → (e2) inspections/airworthiness → (e3) TBO/squawks → (e4) used market | XL | 7c, 7d | The fleet becomes something you **nurse**: a tail can be grounded, fuel and payload trade before launch, a worn plane is worth less. |
| **7f** | **Crew depth** (§4.4) — *branch:* (f1) skill-roll + FTL/fatigue → (f2) roles + market → (f3) player proficiency | XL | 7c, 7e | Crew become a **bench you manage** — a tired captain is a risk, an ace saves a flight, you must own more than one. |
| **7g** | **Bases / living economy** (§4.5) — *strict slice order:* fuel-cost → hangar capacity+upkeep → market coupling → contract lines → credit/insurance depth | XL | 7e, 7f | The map becomes an **economy you operate inside** — the world pushes back every leg. |

**Sequencing rules:**
- 7a blocks nothing and has the highest felt-depth-per-effort in the phase — merge it immediately.
- Do **not** start 7d–7g before 7b lands; they all read fields it adds.
- 7c is the pivot; sequence it right after 7b or every later XL ships inert.
- FDR replay (§4.2 m7) and pre-flight gates (m8) are the lowest depth-per-effort items — fold in as polish after 7c, never blocking the path.
- Every slice re-asserts Law 5 (additive migration + guard test, watermark-seeded RNG, no-op config defaults, dedupe ledger) as its own acceptance criteria.

---

## 6. Deliberately deferred — Phase 8 candidates (named, not forgotten)

The review flagged real depth the five systems still miss. We are **not** doing these now; we name them so the omission is a decision, ranked by realistic-and-fun payoff:

1. **Weather as an active layer** — *the single biggest remaining gap.* Density altitude cutting useful load/climb; headwind eating the deadline clock and fuel reserve; IMC requiring instrument rating/currency; crosswind limits at destination. It interlocks with the clock, fuel, diversion, and the stabilized-approach gate. **Top of Phase 8.**
2. **Time-of-day / calendar** — day/night, night-ops gating & currency, curfews/slots, a circadian dimension to fatigue. Interlocks crew + jobs + the clock.
3. **Economy cycles / seasonal demand** — fuel shocks, seasonal charter swings, downturns that tighten fill; gives the living economy a reason to *time* expansion.
4. **Client relationships / reputation-as-market** — named brokers, repeat clients, loyalty tiers; ties VIP comfort + on-time + safety into repeat higher-margin demand.
5. **Operating certificate / regulatory progression** — Part 91 → 135, ops specs, adding aircraft/pilots to the certificate: the natural meta-progression spine.
6. **ATC / airspace / routing** *(lowest priority — tedium risk)* — a light version could feed the survey band and enroute score; a heavy version drifts into paperwork.

---

## 7. Definition of done & re-audit

Phase 7 is "done" when we re-run the depth audit (same adversarial workflow, same NeoFly-4 = 100 bar) and the graded-from-code scores move:

| Domain | Now | Phase-7 target |
|---|---:|---:|
| Flight, Logbook & Live Ops | 32 | **70+** (7a–7c) |
| Missions & Economy | 42 | **68+** (7d) |
| Fleet & Aircraft | 44 | **70+** (7e) |
| Crew, Bases & Progression | 33 | **65+** (7f + 7g) |

But the real done-test is Law 2, felt at the table: **can the player point at one hard decision per system that the state now forces?** If yes, the app finally *simulates* instead of *records*. Craft stays at its ~80 bar throughout (and we spend some of 7's polish budget on the audit's craft smells: the kitchen-sink dashboard, the reused card grids on buy-aircraft/hire-pilot, the pill monoculture, the stray emoji, and the self-praising code comments).

---

*This plan integrates a 13-agent depth audit and a 7-agent system-design pass, reconciled by hand into one pipeline with single owners. It is a map, not a contract — every balance number here is a starting dial, meant to be tuned against play.*
