# Phase 8 — The World: giving the environment agency

**Status:** planned · **Opened:** 2026-08-20 · **Follows:** `docs/phase-7-depth-plan.md` (complete)

---

## 0. Why this document exists

Phase 7 made **your own actions** have consequence — a landing is scored, a tail wears, a crew's skill bites, a market you trade in moves, a loan you can't service defaults. The company's decisions now matter.

Phase 8 makes **the world** matter. Right now the environment is inert: every airport is identical weather, every day is the same, demand never cycles, a client is a faceless one-off, and there is no regulator. The career happens in a vacuum.

> **Phase 7 gave the player agency. Phase 8 gives the world agency back.**

The one-line test for every Phase 8 mechanic: *does something outside the player's control now shape the run, in a way the player must read and adapt to — not just react to their own choices?*

This is a big plan. §1 is the laws (they extend Phase 7's, they do not replace them). §2 is the integration spine. §3 is the six systems. §4 is the build order. §5 is what we consciously defer.

---

## 1. The design laws (extending Phase 7's five)

Phase 7's five laws still hold in full — **score the sim never command it; one real decision per system; realistic-but-fun with an explicit dial; never reopen to disaster (warn first); invariants on every PR (additive migrations, deterministic RNG from persisted watermarks, tunables behind `EconomyConfig`, every money move a dedupe-keyed posting).** Phase 8 adds three:

**L6 — The world is a pure function of (place, time), not stored per-entity.** Weather at EGLL at hour H, the economy phase in week W, a season — these are computed from a deterministic hash of their coordinates, exactly like `MarketService` prices a good. No weather table, no per-airport climate rows. This keeps the world **server-authoritative and reproducible**: two clients (or a headless reconcile) compute the identical world for the same moment, and there is nothing to migrate or sync. Persisted state appears only where the *player's relationship to the world* accumulates (a client's loyalty, a held certificate) — never the world itself.

**L7 — Read the world for the player-flown flight; model it for everything autonomous.** During a live flight the sim already knows the real wind and visibility — so a **scored** leg reads the sim's actual conditions (the L1 way). Planning, forecasts, and autonomous reconcile trips can't read a sim that isn't running, so they use the **synthetic** model. The two must agree in distribution (the synthetic model is the forecast; the sim is the actual), and a scored leg is always graded on what the sim reported, never on the forecast.

**L8 — The world raises stakes; it never hard-blocks the only path.** Bad weather makes a leg harder and riskier and can delay an autonomous trip — it does not delete the player's ability to fly (they can choose to launch into it). A missing certificate gates a *category* of work, never all work. An economy bust thins the job board, never empties it. The world pressures; the player always has a move.

**L9 — one tab, one screen: no page scroll.** Open any tab and everything that matters is visible at a glance — the summary, the primary controls, the current state — in a single viewport, no scrolling the page to find things. This is how a cockpit or an ops board reads: you take it in, you don't hunt. Concretely: the tab lays out to the viewport (a grid/columns that fills the height, not a tall stack); an unavoidably long list — logbook, ledger, a full job board — lives in its **own fixed-height panel that scrolls internally**, so the page frame, the headline numbers, and the actions never leave the screen. The body never scrolls; only a panel does. This binds **every** UI a Phase 8 slice adds — and it is a **retrofit target for the tabs already built**: today most tabs are a tall single column you must scroll, which is exactly the legibility debt the consolidated polish pass pays down. New surfaces are born one-screen; old ones are converted to it. (This is the app's quality bar — image-forward, deep-but-easy, the big calls surfaced up front — expressed as a hard layout rule.)

---

## 2. The integration spine

Phase 7's spine was one ordered `SettlementService` pipeline. Phase 8's spine is **one world clock and one world oracle** that the existing systems consult — we do **not** build six parallel worlds.

- **`GameClock` (the single source of "now-in-world").** Wages, rent, loans, reconcile already run off elapsed wall-clock. Phase 8 introduces a persisted, monotonic **world epoch** (a company's founding instant + accumulated elapsed) so "what day/season is it" is answerable and consistent across the app. Everything time-derived (seasons, economy phase, cert expiry, client cool-downs) reads this one clock. It never runs backwards; reconcile advances it by whole elapsed units exactly as it advances every other watermark.

- **`WorldOracle` (the single source of "what the world is like there/then").** A stateless service — the L6 pure function — answering: `WeatherAt(icao, instant)`, `EconomyPhaseAt(instant)`, `SeasonAt(instant)`. Deterministic hash of coordinates, no stored state, mirrors `MarketService` exactly. Every consumer (job board, settlement, reconcile, market, UI forecast) reads the same oracle, so the whole app agrees on the world.

- **The consumers are the systems already built.** Weather feeds *scoring* (Flight), *fuel* (Aircraft settlement), *delay/risk* (autonomous reconcile), and *the market* (a storm spikes local demand). Economy cycles feed *the job board* and *market pricing*. A client feeds *the job board* (repeat offers) and *reputation*. The certificate gates *the job board* and *route creation*. Nothing new settles money on its own — it all flows through the Phase 7 settlement/reconcile pipelines, which simply now read the oracle.

The rule that keeps this from collapsing into spaghetti: **systems read the oracle; they never read each other.** Weather doesn't know about clients; the certificate doesn't know about the economy phase. They are independent lenses on the same (place, time), composed only at the consumer.

---

## 3. The six systems

### 3a. Weather (the headline — build first)
The world's temperature, wind, visibility, ceiling, and precipitation at a field, as a deterministic function of (icao, instant) with a realistic diurnal + seasonal shape and correlated regional fronts. Consumed four ways, each through an existing seam:
1. **Scoring (Flight, L1/L7):** the sim reports actual wind/visibility on a scored leg (add ambient-wind + visibility to the `TelemetrySnapshot`, the way 7b added G/bank/flaps). A crosswind or low-vis landing widens the touchdown envelope the grader tolerates — a greaser in a 25 kt crosswind is worth more than in calm air; the un-gameable landing grade gains a difficulty multiplier read from the sim.
2. **Fuel (Aircraft settlement):** a headwind on the leg burns more of the `FuelUsedLbs` already recorded — no change to how fuel is measured (the sim already burned it), only a check that the synthetic forecast and the recorded burn are consistent for anti-cheat.
3. **Autonomous reconcile (Ops):** a synthetic storm at a standing-order/route endpoint delays or scrubs a trip (a weather-hold, surfaced in the digest like grounding/forbearance — a warned, not silent, loss), and raises the incident probability for the trips that do fly.
4. **Market (Economy):** severe weather spikes local demand for some goods (a coupling the market pressure system already has the shape for).
   The player-facing artifact is a **forecast** on the Flight/Ops tabs (what the synthetic model predicts), which the sim then confirms or beats. The *decision*: launch into marginal weather for the premium, or wait it out.

### 3b. Time & calendar
The `GameClock` made legible: a world date, day-of-week, and season shown across the app, and the substrate seasons/cycles/expiries key off. The one real decision it unlocks: **timing** — some work (and better weather) clusters by season; deadlines and cert renewals fall on real dates.

### 3c. Economy cycles
A slow macro oscillation (boom → peak → bust → recovery) over the world clock — the *macro* layer above 7g's *micro* market pressure. In a boom the job board is fuller and rewards richer; in a bust it thins and margins compress (but your fixed costs — wages, rent, loans — don't, which is where a bust bites). Deterministic from the clock (L6), config-tunable amplitude/period, and it multiplies the reward/availability the job board and market already compute — no new settlement path.

**Delivered (v1):** `WorldOracle.EconomyPhaseAt(instant)` — a pure sine cycle (`EconomyCyclePeriod` 45 d, `EconomyCycleAmplitude` 0.22 → demand ∈ [0.78, 1.22]) returning `(DemandMult, Label)` where the label is Boom / Bust / Recovery / Slowing (level from `sin`, trend from `cos`). `JobBoardService.RefreshAsync` multiplies every generated offer's reward by `DemandMult` **once, at posting**, then freezes it onto the `Job` (accept re-freezes the quote; settlement pays it — no recompute). Both cargo and passenger sources ride the cycle uniformly. `/api/world` surfaces `EconomyLabel` + `EconomyRewardPct`; the context header shows a Boom/Bust chip with the signed % vs par. Availability-tie-in and market-demand tie-in deferred to 8f. Fuel-price tie-in stays deferred (§5).

### 3d. Client relationships
Jobs stop being anonymous. A **client** (persisted — this is a player-relationship, the L6 exception) accumulates loyalty from the jobs you complete well and sours from failures/late deliveries. A loyal client sends repeat, better-paying, exclusive offers; a burned one stops calling. This is the *demand-side* mirror of the crew relationship 7f built (a green hire appreciates) — a client you serve well appreciates. Threads through the job board (whose offers) and reputation (already built).

**Delivered (slice 1):** `Client` entity (per-company, persisted; unique on `(CompanyId, ClientKey)`) with `LoyaltyMilli` 0..100000 + completed/failed counters. A pure `ClientRoster` (FNV-1a hash of the origin) gives each field a stable cast of named businesses — half place-named ("Rotterdam Freight"), half invented ("Meridian Logistics"); `JobBoardService.RefreshAsync` stamps each offer with its client (key + name), `JobAssignmentService` **freezes** the client at accept, and `SettlementService` loads-or-creates the client and (a) pays a **repeat premium** = `round(baseCents · ClientLoyaltyBonusPct(loyalty))` from *pre-delivery* loyalty as an additive `JobBonus` line (0 below the 25% threshold, ramping to +12% at full), then (b) **moves the bond** — a full delivery builds loyalty (a sharper flight a little more), a partial dings it, a failure sours it hard (−6000). The board (`/api/jobs`) joins the company's clients so each offer shows the client, a loyalty tier (New/Regular/Loyal/Preferred), and the premium it would pay — turning *whose* job to fly into a real choice. The base reward stays frozen; the premium is a bonus on top, never negative. Additive migration `AddClients`. **Deferred to 8d-2:** loyalty-gated *exclusive/repeat* offers on the board (offer-side), a Clients surface/ledger, and time-decay of a neglected client.

### 3e. Operating certificate
A regulatory license (e.g. a commercial/charter certificate) you earn and maintain, gating a *category* of higher-value work (commercial passenger, hazmat) behind a real requirement — a check-ride or an application with a fee and a standards bar (a minimum reputation / clean-incident record). Renewable, expirable (a warned Law-4 lapse). The progression gate that makes the mid-game a *step up*, not just bigger numbers.

**Delivered (slice 1):** `OperatingCertificate` (per-company, persisted, unique `(CompanyId, Kind)`) with two kinds — **Charter** gates VIP charter work, **Hazmat** gates hazardous loads. `CertificateService.ApplyAsync` enforces a standards bar (minimum reputation + a track record of *settled* deliveries) and a **fee** (`LedgerCategory.CertificateFee`), then issues/renews a cert valid for 120 days; renewing early stacks onto the remaining time (no validity burned). The gate is enforced across **all three paths** that fly a gated category — `JobAssignmentService.AcceptAsync` (manual), `RouteService.CreateRouteAsync` (a scheduled route can't be created for VIP/Hazmat without the cert), and the reconcile route loop (a running route is **held** the moment its cert lapses: the unauthorised window is *forfeit* — watermark advances to now, so a lapse can't be banked and reaped with one cheap renewal — surfaced in the digest's `CertLapsed`, Law-4 visible) — and mirrored as a lock reason on `/api/jobs`. Idempotent (Idempotency-Key header → kind-namespaced keyed dedupe; no-key applies are distinct Guid-keyed transactions). The **Airline** tab shows each certificate's held/valid/expired state, days-left, the standards bar with live eligibility, and an Apply/Renew button. Additive migration `AddOperatingCertificates`. **The adversarial review earned its keep:** it caught a HIGH sibling-path bypass (routes flying VIP/Hazmat fee-free — MinReputationMilli for those was left at 0 when the gate moved to the cert system) + a LOW keyed-dedupe kind collision; a follow-up verification then caught the running-route-after-lapse residual — all three fixed with regression tests. **Deferred to 8e-2:** the *proactive* Law-4 renewal nudge ("your certificate lapses in N days") before it actually lapses, a stricter clean-incident bar, and a check-ride path (vs. the pure application). **Two known residuals (both safe — verified):** (1) the forfeit discards the *authorized* pre-expiry slice of the unsettled window too, not just the post-expiry part — player-unfavorable (never overpays), defensible since autonomous income is only realized at reconcile where authority is checked as of *now*; a proper split-at-expiry pays the authorized part. (2) A player who leaves the app open and triggers *no* reconcile at all across the lapse (forgoing interim income), then renews, then reconciles once, still banks the backlog — a pre-existing property of the non-autonomous watermark model (reconcile runs on startup + manual "Process now" only), not specific to certificates.

### 3f. ATC / dispatch friction
The most speculative — deferred within the phase. Enroute/terminal friction (a clearance delay, a reroute, a hold) that adds time/fuel to a leg. Only pursue if 3a–3e land and there's a clean seam; otherwise a Phase 9 candidate.

---

## 4. Build order

Critical path **8a → 8b → 8c**; clients (8d) and certificate (8e) are branches that can land any time after the clock (8b).

- **8a — Weather substrate + scored-leg difficulty (the pivot).** The `WorldOracle.WeatherAt`, the synthetic model, the added telemetry fields, and the scoring difficulty multiplier. This is the biggest single lift and everything else is lighter once the oracle exists. Ship the synthetic model first (planning/forecast), wire the sim-read for scoring second.
- **8b — `GameClock` + calendar surface.** Persist the world epoch, expose date/season. Small, unblocks 8c/8e.
- **8c — Economy cycles. ✅ shipped.** A pure multiplier off the clock into the job board (both sources), frozen at posting. Market-demand + availability tie-ins moved to 8f. Small once 8b exists.
- **8d — Client relationships.** The persisted client + loyalty + repeat offers. Medium; independent of weather.
- **8e — Operating certificate.** The gate + renewal. Medium; independent of weather.
- **8f — Weather → market demand ✅ shipped (slice 1).** `WorldOracle.WeatherAt` now lifts a field's commodity prices when its weather is foul (`EconomyConfig.WeatherDemandFactor(visSm)` = 1 + `WeatherDemandSwing` 0.10 × how-far-below-clear the visibility is), threaded through `TradeService` display *and* settlement (`GetMarketAsync`/inventory/buy/sell) via a neutral-default `MarketService.Quote(..., weatherDemandFactor=1.0)` — so a field with no `Airport` row prices unchanged (the isolation that keeps every existing trade test green). Foul weather thus pays a premium to *sell into* (with a harder landing there, per 8a-2) but costs more to buy. **The lift is one-sided (only ever raises), so the swing is capped at 2×`TradeSpreadPct` (0.10)** — a same-field buy-in-clear / hold / sell-in-foul round trip always loses (the same invariant 7g's market pressure holds; guard test added). UI: a "weather +N%" tag on the Trade board. **8f-2 — autonomous reconcile scrubs ✅ shipped.** Foul weather at a route/standing-order ORIGIN scrubs a share of the accrued trips: `OperationsService.ReconcileAsync` counts, per trip slot (deterministic `WeatherAt` at the origin), how many are weathered out (`WeatherScrubsTrip` = vis < 1.0 sm or wind ≥ 35 kt), and flies only the rest — scrubbed trips earn nothing, wear nothing, and consume no crew duty, but their *time* passes (the watermark advances over the whole window, so they can't be retried/banked). Surfaced in the digest's `WeatheredOut`. **The churn worry didn't materialise:** the scrub is gated on the origin `Airport` row (absent → no scrub, the same isolation as 8f-1's market read), and the existing reconcile tests either don't seed the origin or their cell is clear — all 327 Core tests stayed green with zero churn. **Still deferred:** the 8c market-demand/availability tie-ins.

Each slice keeps the Phase 7 cadence: hand-implemented for coherence, built + `dotnet test` (Core/Host/SimConnect) + `tsc`/`vite` green, migration-guarded, adversarially reviewed after any multi-mechanic or reconcile-touching slice, committed to `main`.

## 5. Consciously deferred

Real METAR/live weather feeds (the synthetic model is server-ready and clean-room-safe; a real feed is an optional later layer). Full ATC simulation (3f). NOTAMs, airspace closures, and TFRs. Passenger-level satisfaction beyond the existing VIP/comfort score. Multi-leg trip planning / connections. Fuel price tied to the macro cycle (a natural 8c extension, but ships after the cycle exists). These are Phase 9+ once the world has a clock, weather, and a face.
