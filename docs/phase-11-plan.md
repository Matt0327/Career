# Phase 11 — "The Airline": the flywheel

## 0. Why this phase exists

Callsign already lets you *become* an airline — you own a `Company` from turn one, hire crew, brand a tail, fly scheduled `Route`s autonomously, and climb a **Startup → Regional → National → International → Flag Carrier** standing (`AirlineService.GetStandingAsync`). But that standing is a static read of static counts (`Aircraft*5 + Bases*8 + Routes*6`), your hired crew appreciate 0.04%/trip in a corner of the Staff tab, and a `Route` books the same frozen annuity forever. Nothing you own *feeds anything else you own*. This is the depth audit's verdict in one system: **records state, does nothing with it.**

Phase 11 adds the missing head, not a new body. It introduces one new engine — the airline's own **operating reputation**, a figure distinct from your personal pilot reputation — that your two edges nobody else has actually move: autonomous scheduled legs pull it **toward the competence of the crew you chose** (OnAir's self-balancing loop, fused to our un-gameable telemetry), while your own player-flown legs move it by their flight **score**, so flying brilliantly yourself pushes the airline's name past what crew alone can reach. That reputation then **compounds demand at your hubs** (stacking on the weather→market coupling, our other edge), **scales how much scheduled service you can run**, and **gates a top-tier Air Operator Certificate** unlocking a real scheduled-passenger network. Around it we make the whole climb **legible as a named journey** — Contract Operator at the bottom, Flag Carrier at the top, each stage showing what it takes and what it opens — so the sandbox finally has a spine to climb.

It never inverts the owner-operator framing the codebase is built on. `Company` stays the one account root "presented to the player as you." You were established from turn one and you stay the operator. Phase 11 is the aspirational *top* of that same ladder — the point at which the assets you have owned all along start feeding each other — with a low-risk contract on-ramp giving the *bottom* of the ladder a distinct feel (a one-plane outfit flying bigger carriers' overflow) without a second identity, a second wallet, or a wage credit from nowhere.

**The new fun in one line:** watching a decision — who you hire, how you fly, where you hub — turn into a flywheel instead of a spreadsheet of independent annuities.

## 1. New design laws

Two additions to the constitution, both encoding fit-pass guards so later slices can't drift. Everything else the fit pass flagged is already covered by shipped laws and earns no new law (freeze-at-posting = L5/L6, the `DemandMult` precedent; no autonomous self-settlement = the Phase-8 spine rule, `ReconcileAsync` owns all offline money; no phantom equity = net worth stays computed; no new progression *currency* = reuse rank/reputation/certificates).

- **L11 — Deepen, don't duplicate.** The airline endgame *extends the identity already named "Airline."* There is exactly one account root (`Company`), one computed standing read model (deepened into the stage journey, never paralleled), one pilot rank, and certificates as the action-gate. New progression is additional *computed contributions*, *frozen multipliers*, and *additive `CertificateKind`s* — never a second persisted tier ladder, a second income loop, a second balance sheet, or an "employee" role the player becomes. Income always credits the one `Company` through the ledger; there is no employer wallet and no sourceless credit. *(Guards fit-pass R1, R2, R6, R7.)*

- **L12 — Reputation you earn, capped at the operation you run.** The airline's operating reputation moves *toward the competence of the crew you chose* for autonomous legs and by *un-gameable telemetry score* for player legs. It therefore can never be farmed above the operation you actually run: over-scaling cheap crew drags your name *down*, not up; a cheated leg costs the most. It is a **separate figure from personal pilot reputation** — autonomous crew competence can never leak into the pilot's SAR/Emergency gate. It converges, never overshoots, and a weakening operation is *coached down gently and warned* (L4/L9), never tanked for something you didn't control. *(Fuses OnAir's self-balancing loop with L7/L9; guards the "cheap reputation-for-hire" and the "pilot-gate leak" pumps.)*

**Carried invariant (no new law needed) — the safe path yields less.** The 11d contract on-ramp is priced *below* the owner margin it substitutes and is *mutually exclusive with owner income on the same leg* — the exact shape of the shipped `RentFlightHourRateBps > wear` / `LeaseWeeklyRateBps > loan-APR` / `UsedMarketFloorFactor > AircraftResaleFactor` guards. Employment flavor is the safe, lower-yield early option, never a higher-yield idle pump. This is a restatement of shipped precedent, not a new constitutional law.

## 2. The systems / slices

Each slice is additive-EF, deterministic, idempotent, ledger-exact (every money move a dedupe-keyed `LedgerPosting`), all tunables in `EconomyConfig` with neutral defaults, clean-room, and shippable + verifiable in-sandbox. The one accepted untested-here branch anywhere in the phase is the live `FlightTracker` producing a player leg's `FlightRecord` (identical to how freelance legs already work); every autonomous path is fully synthetic and covered.

| # | Slice | What it adds | Sim dep |
|---|-------|--------------|---------|
| **11a** | **Operating Reputation engine** *(flagship, pure Core, money-neutral)* | `Company.OperatingReputationMilli` (0–100.0, separate from pilot rep); autonomous legs pull it *toward crew competence* in `ReconcileAsync`, player legs move it by score in `SettlementService` (Fun-Dial band, cheat-ding); a **source-tagged** `AirlineReputationEvent` audit log (Player vs Crew); feeds `AirlineStanding` as a real contribution; per-pass bounded; digest-surfaced with a coach nudge on any fall. **Moves no cash.** | none |
| **11b** | **Career-stage journey** *(pure Core read model — the legibility graft)* | Deepen the standing into a named, climbable ladder — **Contract Operator → Charter Operator → Regional → National → Flag Carrier** — computed from rank + the now-living operating reputation + fleet/bases/routes/net-worth. Publishes each stage's *requirements* and *what it unlocks* ("reach Stage N," never a wall — L8). Subsumes `AirlineStanding`; does not parallel it (L11). | none |
| **11c** | **Reputation → hub demand coupling** *(the flywheel closes)* | Operating reputation multiplies offer generation + pay at your bases, **frozen onto each `Job`/`Route` at posting** exactly like 8c `DemandMult`; stacks on the weather→market coupling; one-sided lift capped by the 8f-1 `WeatherDemandSwing` invariant (a lift never exceeds the round-trip cost it could pump). | none |
| **11d** | **Contract on-ramp** *(the bottom-of-ladder flavor — the employment graft, de-risked)* | A **contract-for-a-carrier job *source*** on the existing board (data-only invented carriers, no new tables): early-stage/rank gated, priced *below* owner margin, **exclusive-with-owner per leg**, reputation-capped. Settles through the **existing** `SettlementService`/`JobAssignment` path with its existing dedupe keys — **no new settlement path, no new account root, no wage credit.** | player leg via telemetry (accepted untested-here) |
| **11e** | **Hub network** | Promote a base → hub (additive `Base` fields); scheduled-route slots scale with **hub level × operating reputation** via a *legible* `EconomyConfig` formula (OnAir's explicit-formula lesson); amplifies 11c's coupling at that hub. Deterministic scheduling; digest-surfaced. | none |
| **11f** | **AOC + scheduled-passenger network** *(marquee endgame content)* | Additive `CertificateKind.AirOperator` extending the `CertificateService` standards-bar + fee + expiry; nullable scheduled-service fields on `Route` (seat capacity, frequency, load factor, seat-class yield); the pax network is a **`Route` variant booked by the existing `ReconcileAsync`** — no new settlement path. Gates the *category* (L8). | player-flown scheduled leg via telemetry; autonomous path fully testable |

## 3. Build order

Engine → make it legible → make it pay → fill the bottom → scale the middle → cap the top:

1. **11a** first, always — the pure, money-neutral foundation everything compounds off. No cash pump surface to guard, only the reputation invariants, every branch testable with `TestDb`/`FakeClock`.
2. **11b** next — cheap, high-legibility: the moment 11a's living figure becomes a visible climb. Pure read model.
3. **11c** — the first place reputation *pays*; the flywheel closes. Frozen-at-posting multiplier, capped lift.
4. **11d** — fills the now-visible bottom stage with distinct flavor; reuses the settlement path, so it opens no new money seam.
5. **11e** — scales the middle; legible slot formula.
6. **11f** — the marquee top: AOC gate + scheduled-pax network, riding `ReconcileAsync`. Last because it carries the only real content weight and the one player-leg sim branch.

Each of 11a–11c is pure Core and fully unit-tested before any Host/UI work. 11d–11f add Host endpoints + minimal UI on the **existing** Airline / board tabs (L9 one-screen-per-tab; no new tabs).

## 4. Consciously deferred

- **Employment as a literal second identity / employer entity / "start unemployed" mode** — *rejected outright* (L11, fit R1): it requires the owner→employee account-root inversion and points at the wrong end of the ladder for an endgame. The employed *feel* ships as the 11d contract *job source*, never a role you become.
- **A second persisted airline tier ladder** — *rejected* (L11, fit R2): we deepen the standing into the stage journey, we do not parallel it.
- **Autonomous crew flying contract-carrier legs** — deferred and constrained: the on-ramp is *player-flown* (you're building *your* name); letting staff farm contract legs offline reopens the reconcile/pump surface (fit R6). If ever added, it must ride `ReconcileAsync`, never a new loop.
- **Crew contract depth** — permanent-vs-short-term wages, severance, synthetic duty/fatigue, cabin-crew `StaffRole` (OnAir's shape) — genuinely additive later; 11a already makes crew *quality* matter (it moves your name). Post-11 tail.
- **FBO service income** (bases-as-FBOs selling fuel/maintenance to other operators) — a natural passive layer, but it needs a counterparty demand model. Post-11.
- **VA-style shared airline / alliances / franchise / published slot marketplace** — valuable and a natural fit for the **online backend (product B)**, but multi-company/server and clean-room-constrained. Post-Phase-11.
- **Explicit idle reputation decay** (client-loyalty-style half-life) — not needed: the convergence loop *already* eases the name toward a neglected/weak operation, and adding a separate unwarned decay would be a punishment surface (L4). Left out deliberately.

---

## 11a — Operating Reputation engine (first slice)

The safe, pure foundation. **Deliberately money-neutral:** 11a writes no `LedgerPosting` and applies no reward multiplier. It moves one new column, adds one audit table, adds one standing contribution, and adds one digest field — so `CashCents == Σ ledger` and `NetCents` are provably unchanged, and there is no cash pump surface, only the reputation invariants. Every branch is testable with `TestDb`/`FakeClock`, no sim.

### Schema
- `Company.OperatingReputationMilli` — `int NOT NULL DEFAULT 0`, thousandths `[0, 100000]` = 0.0–100.0 (the exact scale/idiom of `Pilot.ReputationMilli`). Fresh/legacy DBs open at 0 — an unearned reputation.
- New table `AirlineReputationEvents` — company-scoped mirror of `ReputationEvent` **plus a `Source` tag** (`AirlineRepSource { Player, Crew }`, pinned, persisted, default `Player`). Index on `CompanyId`. Local-only (no `ISyncable`).
- `ProgressMetrics` gains a trailing `OperatingReputationMilli` (positional record, appended).

### The two pure helpers (`EconomyConfig`)
- `OperatingRepPlayerDeltaMilli(overallScore, scored, valid)` — cheated leg ⇒ worst move; great leg (≥ high) ⇒ gain; poor leg (< low) ⇒ ding; coaching-band leg ⇒ 0 (L9); unscored ⇒ 0 (L10).
- `OperatingRepCrewPullMilli(currentRepMilli, crewSkillMilli, trips)` — a fraction of the gap *toward crew competence*, proportional to trips; a poorer crew than your name pulls it **down** (the L12 cap). Caller sums pulls across batches and clamps the net to ±`OperatingRepAutoMaxStepPerPassMilli`.

### Seams
- **Player leg** — `SettlementService.SettleAsync`, after the pilot-rep block, inside the same settlement transaction; mirrors the pilot `ReputationEvent` write with `Source = Player`.
- **Autonomous legs** — `OperationsService.ReconcileAsync`, rides the reconcile watermark like `SharpenCrew`; accumulate `OperatingRepCrewPullMilli` per crew batch against rep-as-of-pass-start, clamp the net, apply + audit (`Source = Crew`) before the terminal `SaveChangesAsync`.
- **Standing** — `AirlineService.GetStandingAsync` gains one contribution `("Airline reputation", m.OperatingReputationMilli / 1000)`; a 0-rep company hides the lever (existing behavior preserved).
- **Digest** — `ReconcileDigest.OperatingRepDeltaMilli` (trailing default) surfaced in the reopen digest (L4).

### Money-pump guards
Self-balancing convergence (no one-sided farm); separate `Company` column (no pilot-gate leak); settlement `Status==Settled` + reconcile watermark (no replay); per-pass net clamp (no teleport); **no money moved** (no cash pump); `valid` gate (cheat-ding); `!scored ⇒ 0` (no legacy-leg move).
