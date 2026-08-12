# Phase 3 plan — earning your wings (progression)

Phase 1 gave you a flying loop; Phase 2 gave you a company. Phase 3 gives your **pilot a career**:
experience earns **ranks**, check-flights earn **class ratings**, and those gates unlock bigger
aircraft and the **full mission roster** — always shown *locked with the reason*, never hidden. The
flat "any job in any plane" board becomes a ladder you climb.

Today these bones exist but are inert: `Pilot.Rank`, `Pilot.Xp`, and `Pilot.ReputationMilli` are stored
(XP even accrues on settlement) but the rank never advances, reputation never moves, `Job.RequiredRank`
never gates, and only 2 of the 11 `MissionType`s are generated. Phase 3 brings them to life.

> **Plan reshuffle, for the record.** [`domain-notes.md`](domain-notes.md) §4.3 tagged rank /
> qualifications / check-flights as *P2*. In the build, Phase 2 became "the company you run" (ownership,
> costs, staff, bases, passengers, trade), so the **progression** pillar slid to Phase 3. The data model
> in §4.3 / §6.10 already anticipates all of it.

The theses hold throughout: **gates are filters, never reward multipliers** — a job you can't take is shown
locked *with the reason*, not removed (critic #14, §6.10). **Prices stay economy-computed**, never
player-set. Money only moves through the **ledger**. Schema changes ship as **EF migrations** (no save
wipe). Everything stays **server-ready**.

## Build order — each step independently playable, finish before the next

1. ✅ **3a — Rank tiers & promotion.** `RankTiers` reference content (per `PilotRank`: `MinXp`,
   `DisplayName`, `Description`), self-documenting and shipped to the UI via `/api/ranks` so no threshold
   is hard-coded. Settlement recomputes rank from cumulative XP and returns `PromotedTo` on a crossing;
   the settlement toast celebrates it and the dashboard shows a *progress-to-next-rank* bar. No new money.
   *Verified live:* flew legs to 518 XP → promoted to Copilot. (`UnlocksMissionMask` arrives with 3b/3e.)

2. ✅ **3b — Job gating by rank (locked-with-reason).** Generators now stamp `Job.RequiredRank` by leg
   difficulty (distance bands → Copilot / Captain / Senior Captain), so the ladder is visible with today's
   cargo & passenger jobs. `/api/jobs` returns every job but flags the ones above your rank as `locked`
   with a reason; the board dims them and swaps Accept for the reason. `AcceptAsync` refuses server-side
   with the same message. *Verified live:* a Trainee's board showed Copilot/Captain/Senior-Captain jobs
   locked, and accept returned `400 "Copilot required — you're a Trainee."`

3. ✅ **3c — Qualification classes & aircraft gating.** `QualificationClasses` (letter classes A–M,
   H = helicopter, M = glider), self-documented via `/api/quals`; each aircraft category maps to the class
   it needs (derived from `AircraftType.Category`, no stored column). `PilotQualification` (save + EF
   migration `AddPilotQualifications`) records the classes you hold with `Stars`. A new career is seeded
   Class A so the gifted single is flyable. The hangar flags each airframe **rated / not rated**; the
   flight picker defaults to a rated aircraft and disables the rest; dispatching an unrated one is refused
   server-side. *Verified live:* a Class-A pilot flew the starter but got `400 "…not rated… needs Class C ·
   Turboprop"` on a bought turboprop. (Stars/earning come with 3d.)

4. ✅ **3d — Check-flights (earn a rating).** Pay a `CheckFlightFee` (ledger debit, rising by class) to fly
   a scored landing that earns or upgrades a `PilotQualification` — `CheckFlightService` grades the touchdown
   (≤200 fpm passes, 3–5 stars by how clean), keeps the best fpm, and bills the fee pass *or* fail. The live
   loop grades a check-ride on landing (`BeginCheckFlight` → a `checkflight` WS event), with a manual submit
   endpoint alongside; the Flight tab lists check-rides to book and shows the result. *Verified live:* a
   greaser passed Class C at 5★ ($8k fee), a slam failed Class D (fee still charged), and the earned Class C
   flipped a bought turboprop to **rated**.

5. ✅ **3e — The full mission roster.** A data-driven `MissionCatalog` (per-type reward/XP multipliers,
   reputation reward, board share, rank gate, labels) drives one generic `MissionJobSource`, mixed by the
   existing `CompositeJobSource`. Ships the rank/aircraft-gated types — **Express, Tourist, Sensitive,
   Hazardous, VIP** — alongside cargo & passenger; each is priced off the one economy engine × its
   multiplier, and its rank gate is the harder of the leg's distance rank and the mission minimum. The
   reputation gate is wired (board lock + accept), dormant at 0. *Verified live:* a 24-job Trainee board
   mixed all seven types, locked-with-reason, with a 96 nm Hazardous correctly floored to Captain.
   (Reputation-gated **Emergency/SAR** and **Illicit** arrive with 3f.)

6. ✅ **3f — Reputation in motion.** Settlement now nudges `Pilot.ReputationMilli` by the mission's
   reputation reward (stored in thousandths so tiny gains survive), itemised into an append-only
   `ReputationEvent` log (EF migration `AddReputationEvents`). The reputation-gated missions go live —
   **Emergency** and **Search & Rescue** unlock once your reputation is high enough, **Illicit** is always
   on offer but *tanks* it (a real trade-off) — enforced on the board lock and at accept. The dashboard
   shows reputation + a recent-events card. *Verified live:* a delivery moved reputation and logged the
   event; the rep-gated missions showed locked-with-reason. (A follow-up tracks making sure every refresh
   offers a Trainee some accessible work.)

## Invariants (carried from Phases 1–2, enforced at every step)

- Every money movement is a `LedgerEntry`; `Company.CashCents` is only a cache of the ledger.
- Rank / qualification / reputation are **filters**, never reward multipliers. Locked jobs are shown with
  the reason, never hidden.
- Prices are always economy-computed (never player-set), even for check-flights and new mission types.
- Reference/content (`RankTierDef`, `QualificationClassDef`) is self-documenting (`Description` shipped in
  the row) and server-suppliable.
- Schema changes ship as EF migrations; money endpoints stay idempotent.

## Explicitly deferred (Phase 4+)

Bases/routes are done; still ahead: **routes** as scheduled repeatable work, **loans** (liability tracked
separately from cash), **insurance** (policy + claim path), a computed **net-worth / P&L** view, and the
**shared-world ADR** → **Phase 4**. Company/airline identity, campaigns, and backup/settings → **Phase 5**.
