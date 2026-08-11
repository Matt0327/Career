# Project Callsign — Domain Notes (Internal Design)

> **Status:** living design reference. Internal-facing. Merges four analyses (data model, foreclosure audit, economy design, UX-divergence strategy) into one direction for `Callsign.Core`, then folds in a fifth pass — a design-critic review whose fifteen issues are resolved or explicitly recorded in §12.
> **Ground truth checked (2026-08-11):** current Core = `Pilot`, `LedgerEntry`/`LedgerCategory`, `LedgerService`, `CallsignDbContext`, `NewGameService`, `Clock`. Confirmed against source:
> - **There are no EF migrations in the repo** — the schema only materialises via `EnsureCreated()` (`tests/…/TestDb.cs`), and no save has ever shipped. Every schema change below is therefore *free today* and, because the ledger is append-only, *permanently expensive after the first shipped save.* That is why this document exists now rather than at Phase 4.
> - `LedgerCategory` already contains `Fuel` and `Repair` but **not** `AirportFee` or `JobBonus`; it is a plain enum with **no pinned integer values**, and neither is `PilotRank`.
> - `Pilot.CashCents` has a **public setter** — any code can move money without a ledger row today; only a test guards `cache == Σ ledger`.
> - `LedgerEntry.PilotId` uses **`OnDelete(DeleteBehavior.Cascade)`** — deleting the pilot deletes the money.
> - `LedgerService.PostAsync(pilotId, …)` writes **one row per `SaveChangesAsync`**; a multi-row settlement is not atomic.
> These facts are the reason several deltas below are "now": they touch entities that already exist.

---

## 1. Purpose & clean-room note

This is the shared model behind the flying loop and the company loop: the entities, the money rules, the scoring, and the ways our UX deliberately diverges from the genre norm. It exists so that Phase-1 code doesn't quietly foreclose Phases 2–5 or the optional shared world.

**Clean-room boundary — read before adding anything here.** Everything in this document describes *economic and simulation mechanics*, which are genre conventions and not protectable: a running-balance ledger, buy-vs-rent aircraft, aircraft that physically sit at airports, supply/demand goods trade, tiered loans, reputation gates, lettered qualification classes earned by a check flight, landing scored on touchdown rate, per-airport fuel and fees. We implement those systems. We do **not** reproduce any observed product's presentation: no screen layout, column order, colour, icon set, terminology, status tag, or asset. Where a mechanic is described, its *presentation is designed from scratch*. Competitor names are kept out of this doc except where a single reference is unavoidable ("the incumbent add-on", "the sim's bundled career mode", "an FSEconomy-style shared world"); they never appear in shipped UI, and we never touch a competitor's save data.

---

## 2. Confirmed direction (the load-bearing commitments)

All analyses converged on these. They are decisions, not options.

1. **The ledger is the single source of truth for *cash* — and only cash.** `Σ LedgerEntry.AmountCents` is the authoritative balance and is never computed a second way. But a loan's outstanding principal, an inventory lot's cost basis, a held rental deposit, and an aircraft's residual value are **not** expressible as sums of cash movements. So: **cash lives in the ledger; assets and liabilities are their own entities; net worth is a *computed view*, never stored.** The net-worth view is
   `net worth = cash − debt + aircraftResidual + inventoryAtCost + recoverableDeposits`
   — the last term matters because a paid rental deposit is cash that left the ledger but is recoverable, so omitting it understates net worth until refund (critic #14). This refinement of "the ledger is truth" is load-bearing and easy to get wrong — every recurring interest realisation still posts a ledger row, but `Loan.OutstandingCents` tracks the liability separately, and an asset *loss that moves no cash* (spoilage, hull write-off) posts **zero** ledger rows and instead disposes the asset entity (§6.5, §6.11, critic #9).

2. **Money belongs to a career-level account, never to the player `Pilot` and never to a staff pilot.** Introduce a `Company`/account root in Phase 1, seeded as exactly one row (= the player). The ledger, aircraft ownership, wages, loans, and bases all hang off it from day one, so nothing has to be re-parented when staff (P3) and the airline (P5) arrive. In P1 the UI still presents it as "you."

3. **Quote / Settlement split with freeze-on-commit.** Offers (job reward, aircraft price, loan APR, commodity price, **fuel price, airport fee**) are *quotes* generated behind interfaces and **frozen onto the record at the moment the player commits**. What actually happened (you landed at −95 fpm, overspeed once, delivered late) is *settlement* — always local, deterministic, reconciled against the frozen quote. Settlement **reads the frozen quote on `JobAssignment`, never the live `Job`** (whose `RewardCents` can be regenerated), so "the number you saw is the number you get" holds by construction (critic #11). This single seam delivers two things at once: the QoL promise, and server-readiness (a server can supply quotes wholesale without touching settlement).

4. **Server-readiness is a Phase-1 constraint, not a Phase-4 retrofit.** Generation sits behind `IJobSource` / `IPriceProvider`; **no entity or screen assumes prices are locally generated — this explicitly includes fuel prices and airport/landing/parking fees, which are per-airport quotes from the provider seam, not local formulas** (critic #1, #10). Generated content carries `GeneratedAt` + `ExpiresAt`; syncable aggregates carry dormant `UpdatedAt` / `IsDeleted` / reserved `OriginClientId`. The addendum leans toward a *read-mostly* shared layer (shared prices/demand/fees, private aircraft/progression); that split maps cleanly onto our schema (see §9, §10).

5. **Offline progression = deterministic function of elapsed wall-clock time, per obligation.** Every recurring obligation (wage, rent, owning-day, loan payment, insurance premium, rental day/hour, parking, rental **expiry**) is `(anchor, period, watermark)` advanced from its *own* last-processed time to `IClock.UtcNow` on tick and on load — **never one shared watermark for all of them** (critic #3). A newly hired pilot anchored at `HiredAt` and a mid-week loan at `TakenAt` cannot be billed from a single `Company.LastTickAt`; each obligation therefore carries its own `LastBilledAt` (or is reconstructed from `anchor` + `DedupeKey`-existence, which is the documented, tested fallback contract). The whole thing must be **idempotent** (a re-run can't double-post). Two things are explicitly *not* wall-clock schedules and are modelled separately: **hours-based maintenance** advances off an `AircraftInstance.AirframeHours` watermark bumped by flights, not elapsed time; and **rental expiry** is a scheduled *state transition* (auto-return + deposit reconciliation + ferry fee), not a recurring charge (§6.6, critic #8).

6. **Legibility is the product.** Every payout and charge is itemised, each line mapping 1:1 to a ledger row; every price exposes its factors; every recurring commitment shows break-even *before* confirmation. The break-even preview and the biller draw from the **same** projection service, and that projection now includes **fuel and end-of-rental costs** so the preview matches reality (critic #1, #8). Every gate states its reason and the path to open it. The app is self-documenting at the point of use — no external manual, first flight in ten minutes.

7. **Auto-scan the installed fleet; compute flyability.** The roster is built by scanning the sim's own packages (never a hand-curated checklist), and "can you actually fly this listing?" is *computed against that scan and stated in plain language* (never a cryptic version tag). **Install/scan state is machine-local, not part of the shared type identity** — it lives in a local-only `InstalledPackage` side table so the LAN companion, a second PC, and a future shared type catalog aren't broken by a per-machine flag welded onto reference data (critic #4).

8. **One coherent product.** Flying loop and company loop are the same app over the same data and the same ledger. No paid-module fragmentation, no separate companion codebase (it is the same backend over LAN), no registration wall before the first flight.

9. **No numeric literals in economy code.** All tunables live in a single versioned `EconomyConfig` record tree saved with the game; a retune ships as a new config version while existing frozen quotes keep their terms.

10. **Atomic settlement.** A settlement that writes several ledger rows plus record side-effects (breakdown JSON, XP, condition hits) is a **single transaction** — `LedgerService` grows a batch/transactional post so `Σ breakdown lines == Σ ledger rows` is written and asserted atomically, never as N separate commits that a crash could tear (critic #2).

### Divergences between the source analyses, now resolved

- **Reputation scale.** One analysis proposed hundredths (`0..10000`), another thousandths. Observed gains are as fine as **+0.028 per flight**; hundredths rounds that to noise (2.8) and the Phase-3 offline reward loop silently never moves. **Resolved: store reputation as integer *thousandths* (`ReputationMilli`, `0..100000` = `0.000..100.000`).**
- **Whose reputation?** `ReputationMilli` lives on `Pilot`, `Staff`, and `Company`, which are three different things (critic #5). **Resolved:** `Pilot` = the player's personal flying reputation (gates/premiums in solo play); `Staff` = each hired pilot's personal reputation (the +0.028 autonomous gain lands *here*); `Company` = the airline's standing (P5, customer quality). Every gate/effect in §6.7 binds to a **named** owner: goods locks, credit limit, mission unlocks, reward premium, fee/APR discounts read the **operating entity's** reputation — the `Pilot` in solo P1–P2, the `Company` once an airline exists (precedence: Company if present, else Pilot). Staff reputation never gates goods; it only scales that staff member's own mission quality and payout.
- **Is the *ledger itself* ever server-authoritative?** `LedgerService`'s doc-comment says a server "could become authoritative over the ledger"; the addendum leans read-mostly (money stays local). **Resolved for now:** treat read-mostly as the working assumption, but take the cheap insurance anyway — `EntryUid: Guid` doubles as the sync/idempotency key and costs almost nothing. Whether the money ledger ever goes shared is a Phase-4 ADR question (§11).

---

## 3. Cross-cutting conventions (assumed by every entity; not repeated per-entity)

| Concern | Rule |
|---|---|
| **Money** | Integer minor units ("cents") of a display currency ("cr") in a `long` field with a `*Cents` suffix. Never `float`/`double`/`decimal` in storage. `decimal` is allowed only at a service boundary (as `LedgerService.PostAsync` does today) and converted immediately. |
| **Reputation** | Integer thousandths in a `*Milli` field (`0..100000`). Display as `value/1000`. |
| **PK — syncable aggregate** | `Id: Guid` (client-assignable, collision-free), for anything a shared-world server might own or a backup might merge: Company, Pilot, AircraftInstance, Job, JobAssignment, Flight, Staff, Base, Route, Loan, Stock, etc. |
| **PK — shipped reference data** | Natural key, no Guid: `Airport.Icao`, `Commodity.Code`, the `*Def` tables. Bundled datasets, replaced wholesale on update, never synced. |
| **PK — the ledger** | Special-cased (see §5). |
| **Local-only machine state** | Anything that describes *this PC's* install/scan (not the save, not the type) lives in a **local-only, never-synced** table keyed by the shared entity plus a `HostClientId` — e.g. `InstalledPackage` for the aircraft scan. It carries no `UpdatedAt`/`IsDeleted` (nothing to merge). Keeps shared reference data clean (critic #4). |
| **Enums** | Stored as `int` with **explicit pinned values** (`= 1, 2, …`) so reordering can never corrupt stored rows — *except `LedgerCategory`*, stored as a **string** (`HasConversion<string>()`) so it can grow per-phase safely and the SQLite file is self-documenting. **`[Flags]` enums use explicit powers of two.** A pinning test asserts the numeric mapping of every persisted enum. This is not aspirational: the current `PilotRank` and `LedgerCategory` have *no* pinned values and must get them before any save ships (critic #12). |
| **JSON columns** | `*Json: string` holding a serialized DTO, used only for append-only, read-as-a-blob detail (scored events, payout lines). Never for anything queried or summed relationally. |
| **ICAO refs** | `string` (len ≤ 8), a **soft FK** to `Airport.Icao`, not an enforced constraint, so the airport dataset can be reference-updated independently of saves. |
| **Time** | `DateTimeOffset` (UTC), always via `IClock` (`SystemClock`/`FakeClock`) so offline catch-up stays testable. |
| **Sync/backup** | Every syncable aggregate carries `UpdatedAt: DateTimeOffset` + `IsDeleted: bool` (soft delete — we never hard-delete). Reserve `OriginClientId: Guid?` (leave null) so a server can become authoritative without a schema break. |
| **Generation provenance** | Generated content (`Job`, `MarketListing`, `MarketPrice`, `FuelPrice`, `AirportServiceFee`, offers) carries `GeneratedAt` + `ExpiresAt` and never embeds "computed here" assumptions. |
| **Snapshot / legibility invariant** | Any figure shown before a commitment (fee, quote, break-even input, **fuel unit price, projected end-of-rental cost**) is **snapshotted onto the record**, so the figure shown equals the figure charged. The ledger is the sole reconciliation point for the cash figures. |

---

## 4. Full-game data model (by phase)

Phase legend: **P1** flying loop · **P2** market + qualifications + all job types · **P3** staff + autonomy + offline · **P4** bases + routes + trade + loans + P&L (+ shared-world ADR) · **P5** company/airline + campaigns + backup/settings/companion.

### 4.1 Account root — `Company` (P1, expands P5)

The one money/asset owner. Seeded as a single row in P1.

```
Company                      (syncable aggregate)
  Id: Guid
  Name: string               // P1 the player's name; P5 the airline name
  HubIcao: string            // soft FK; P1 = pilot home
  CashCents: long            // cached balance — PRIVATE setter, mutated ONLY by LedgerService (§5)
  Version: int               // concurrency token (IsConcurrencyToken) — the two loops race on the cache
  ReputationMilli: int       // P5 company reputation (see §2 "whose reputation")
  LogoRef: string?           // P5 our own asset path, never an external URL
  CreatedAt: DateTimeOffset
  LastTickAt: DateTimeOffset  // P3 coarse tick anchor ONLY; per-obligation billing uses each obligation's own LastBilledAt (§2.5)
```

`CashCents` is the same cache pattern the current `Pilot.CashCents` uses — moved to the correct owner **and encapsulated**: the setter is private/internal and mutated only through `LedgerService`, backed by a runtime self-heal + assert (critic #11, §5).

### 4.2 `LedgerEntry` — see §5 (its own section; it is the spine).

### 4.3 Pilot, ranks, qualifications, reputation

```
Pilot                        (P1, syncable)
  Id: Guid
  CompanyId: Guid            // the account this career belongs to
  Name: string               // callsign
  Rank: PilotRank            // enum WITH pinned values; thresholds live in RankTierDef (P2)
  Xp: int
  ReputationMilli: int       // was int Reputation — the player's personal flying reputation
  HomeIcao / CurrentIcao: string   // soft FKs; CurrentIcao updated on arrival
  TotalFlyTimeSeconds: long  // cache; derivable from Flights
  // CashCents REMOVED — moves to Company. Keep a [NotMapped] passthrough if UI wants "your cash".
```

Derived career stats (best XP/reward, most-used aircraft, landing-fpm trend) are **queries over `Flight`/`LedgerEntry`, never stored** — an optional `PilotStats` cache (P2) must be reconstructible.

```
RankTierDef                  (P2, reference/content)
  Rank: PilotRank (PK) · MinXp · DisplayName · Description   // Description shown in-app (self-documenting)
  UnlocksMissionMask: int    // [Flags] MissionType gated by rank (powers of two)

QualificationClassDef        (P2, reference/content)   // letter classes A,B,C,D,E,F,H,M (H=helicopter)
  Class: QualClass (PK) · Title · Description           // e.g. "single-engine piston / EV-hybrid, cruise < 150 ktas"
  MaxCruiseKtas: int? · EngineTypesMask: int?           // gating attributes are DATA, not hardcoded
  DefaultTestAircraftTypeId: Guid?

PilotQualification           (P2, save)
  Id · PilotId · Class · Stars(0..5) · BestTouchdownFpm · EarnedAt · CheckFlightId?

TestLocation                 (P2, generated/reference)   // where a class can be earned
  Id · Class · AirportIcao(soft FK) · City · TestAircraftTypeId · FeeCents(snapshot) · ExpiresAt?
  // distance-from-pilot is COMPUTED at query time, never stored

CheckFlight                  (P2, save)                   // an attempt
  Id · PilotId · Class · TestLocationId · TestAircraftTypeId · FeeCents
  AttemptedAt · Passed · ResultStars · BestTouchdownFpm · FlightId?

ReputationEvent              (P4, append-only, save)      // itemised reputation log (gains are tiny/opaque otherwise)
  Id · OwnerType(Pilot=1|Company=2|Staff=3) · OwnerId · At · DeltaMilli(signed) · Reason · RelatedEntityId?
  // OwnerType INCLUDES Staff so a staff pilot's +0.028 autonomous gain has a home (critic #5)
```

### 4.4 Aircraft — type (shared) vs instance (owned) vs install (local)

```
AircraftType                 (P1 subset → P2 complete; SHARED reference catalog — clean identity only)
  Id: Guid
  CanonicalName: string
  AliasesJson: string        // string[] of normalized titles → feeds AircraftIdentity.Matches
                             //   (the live PoC "PC-12NGX Cargo - Empty" livery/variant case)
  RequiredClass: QualClass   // P2 category letter ↔ qualification gate
  CanFlyMask: int            // P2 [Flags] MissionType this airframe can carry (powers of two)
  Seats · UsefulLoadLbs · CargoCapacityLbs · FuelCapacityLbs · CruiseKtas · RangeNm · MinRunwayFt
  IsHelicopter: bool         // P2 (class H)
  FuelType: FuelKind         // Avgas | JetA | Electric — selects the per-airport fuel price (§6.10)
  // NOTE: IsInstalled / SimSource / ScannedAt are DELIBERATELY NOT here — they are machine-local (see below)
```

```
InstalledPackage             (P1, LOCAL-ONLY — never synced, no UpdatedAt/IsDeleted)   // critic #4
  AircraftTypeId: Guid · HostClientId: Guid          // (this PC)
  IsInstalled: bool          // set by the SCANNER, not the user
  SimSource: SimSourceTag    // Sim2024 | Sim2020 | Community | Curated — replaces the cryptic "2024-S/2020-S"
  ScannedAt: DateTimeOffset
```
Install state is a property of *this sim PC*, not of the type, the save, or a shared catalog: the LAN companion phone has no installs, two PCs differ, and a shared type catalog cannot carry a per-machine flag. Splitting it out keeps `AircraftType` clean shared reference data; `MarketListing.IsFlyableByPlayer` is computed by joining `InstalledPackage` (local) + the pilot's qualifications, so the app can *always* state whether a listing is flyable — directly fixing the observed "can't tell if you own it" failure.

```
AircraftInstance             (P1 stub → P2 ownership → P3 crew; syncable)
  Id: Guid · TypeId: Guid
  CompanyId: Guid?           // owner; null while an unowned market listing
  Tail: string
  Ownership: OwnershipKind    // Owned=1 | Rented=2 | Listed=3
  Availability: AircraftAvailability  // Available=1 | Reserved=2 | InFlight=3 | Grounded=4   (critic #6)
  LocationIcao: string        // soft FK — where it physically sits when NOT InFlight
  EnRouteToIcao: string?      // set while Availability=InFlight; LocationIcao holds the origin until arrival
  ReservedByAssignmentId: Guid?  // single-occupancy guard: who holds it
  HullConditionMilli · EngineConditionMilli: int   // 0..100000; P1 stubbed at full
  FuelOnBoardLbs: int · AirframeHours: double       // hours tick per flight (P2)
  MaintenanceHoursWatermark: double                 // last airframe-hours value maintenance was billed at (§6.6)
  // rental terms (P2) — all snapshots so break-even can be shown before signing
  PurchasePriceCents? · RentalPerOwningDayCents? · RentalPerFlightHourCents? · RentalDepositCents? · RentalUntil?
  RentalLastBilledAt?         // per-obligation watermark for owning-day/flight-hour accrual (§2.5)
  AcquiredAt?
  // crew (P3)
  AssignedStaffId: Guid? · StandingOrderId: Guid?

AircraftCrewSlot             (P3)
  Id · AircraftInstanceId · Slot(Copilot|FlightAttendant|Loadmaster) · StaffId?
```
`Availability` + `EnRouteToIcao` + `ReservedByAssignmentId` close a correctness gap in the offline autonomous loop: without a reservation state, a second standing order (or the player) can double-book the same airframe for wall-clock hours, and `LocationIcao` is ambiguous mid-flight (critic #6). Single-occupancy is enforced at assignment/standing-order time.

### 4.5 Market — listings, pricing, fuel & airport services

```
MarketListing                (P2; server-ready)          // buy/rent an aircraft at an airport
  Id · AircraftInstanceId · AirportIcao(soft FK) · Offer(Buy|Rent|Both)
  BuyPriceCents? · RentPerOwningDayCents? · RentPerFlightHourCents? · RentDepositCents?
  PriceFactorsJson: string   // itemised "why this price" [{factor, deltaCents}] summing to the quote
  IsFlyableByPlayer: bool    // derived from InstalledPackage (local) + qualifications — no cryptic source tag
  GeneratedAt · ExpiresAt?

Commodity                    (P4, reference/content; our own goods list)
  Code: string (PK) · DisplayName · UnitWeightLbs · Category · MinReputationMilli · BaseValueCents
  // "fuel" as a COMMODITY (a good you haul to sell) is a distinct system from consumable tank fuel — see §6.10

MarketPrice                  (P4; server-ready — this IS the read-mostly shared layer)
  Id · AirportIcao · CommodityCode · Side(Buy|Sell)     // two separate books
  UnitPriceCents · Quantity · RefreshedAt · ExpiresAt(refresh TTL) · PerishExpiresAt?

FuelPrice                    (P2 relevance → P4 shared; generated, server-ready)      // critic #1
  Id · AirportIcao(soft FK) · FuelKind(Avgas|JetA|Electric)
  UnitPriceCentsPerLb · GeneratedAt · ExpiresAt
  // consumed by refuel (§6.10); a fill snapshots the unit price at time of fill onto the Fuel ledger row

AirportServiceFee            (P2 relevance → P4 shared; generated, server-ready)      // critic #10
  Id · AirportIcao(soft FK) · Kind(Landing|Parking|Handling)
  AmountCents · GeneratedAt · ExpiresAt
  // the general per-airport fee source for NON-job arrivals, owned-aircraft parking, and autonomous flights;
  // derived from size/runway/elevation, snapshotted at charge time, server-suppliable — never a local formula

Stock                        (P4, save)                  // goods physically held → cost basis for profit
  Id · OwnerType(AircraftInstance|Base) · OwnerId · CommodityCode · Quantity
  AcquiredPriceCents(cost basis) · AcquiredAtIcao · AcquiredAt · PerishExpiresAt? · IsDeleted
  // spoilage / undelivered = soft-delete the lot (asset disposal), NOT a ledger row (§6.5, critic #9)
```

### 4.6 Jobs, assignments, passengers

```
Job                          (P1 Cargo subset → P2 all types; server-ready via IJobSource)
  Id · Source(Generated|Freelance|Community|Campaign|CustomCompany) · Type: MissionType
  OriginIcao · DestIcao · DistanceNm · HeadingDeg(P2) · WeightLbs · PaxCount(P2)
  RewardCents · Xp            // ALWAYS economy-priced via IPriceProvider — never player-set, even for
                             //   CustomCompany/Route jobs, or it is a money-printing exploit (critic #14)
  DestFeesCents(P2, snapshot) · DestLongestRunwayFt(P2) · DestElevationFt(P2)   // planning info shown = charged
  RequiredClass: QualClass?(P2) · RequiredRank: PilotRank?(P1)
  LoadByAt?(P2 deadline) · MaxFlightTimeSeconds?(P2, e.g. "must not exceed 1h46")
  ExpiresAt(board TTL) · DescriptionText · GeneratedAt

MissionType  [Flags]  =  Cargo=1, Express=2, Passenger=4, Tourist=8, Sensitive=16, Hazardous=32,
                         Emergency=64, SAR=128, Parachute=256, Advertising=512, Illicit=1024   // P1 uses only Cargo

JobAssignment                (P1; the state machine settlement reconciles against)
  Id · JobId · AircraftInstanceId
  AssignedPilotId? · AssignedStaffId?          // player pilot XOR staff (P3)
  Status(Accepted|Loaded|InProgress|Delivered|Settled|Failed|Expired)
  AcceptedAt · RewardQuoteCents · FeesQuoteCents    // FROZEN at accept — settlement reads THESE, not live Job
  FlightId?                                          // many-assignments-per-flight allowed (multi-load)

Passenger                    (P2)                      // pax manifest line
  Id · JobId · Name · Sex · Age · WeightLbs · Seat? · OriginIcao · DestIcao
  Status(Manifested|Boarded|Delivered|NoShow)
```

### 4.7 Flight — the scored record (headline: touchdown fpm)

```
Flight                       (P1 player; P3 adds autonomous/staff; syncable)
  Id · AircraftInstanceId
  FlownByPilotId? · FlownByStaffId?   // EXACTLY ONE non-null
  DepartureIcao · ArrivalIcao · DepartedAt · ArrivedAt?
  DistanceNm · BlockSeconds
  TouchdownFpm: int                    // HEADLINE recorded metric (e.g. -84, -274, -405)
  FuelUsedLbs · MaxGForce?(P1e) · Exceedances: int
  PayoutCents: long                    // net; equals Σ of this flight's ledger rows
  PayoutBreakdownJson · EventsJson     // shapes in §7
  Result(Completed=1|Diverted=2|Crashed=3|Abandoned=4) · IsAutonomous: bool(P3)
  // Result=Crashed triggers the total-loss money/asset path (§6.11); an autonomous crash is processed
  //   deterministically on offline catch-up and surfaced in the reopen digest (critic #7)
```

### 4.8 Staff, standing orders, bases, routes, loans, insurance, campaigns, settings

```
Staff                        (P3, syncable)             // never owns cash; paid via the ledger
  Id · CompanyId · Name · Role(Pilot|FlightAttendant|Manager|GroundCrew)
  HomeIcao · CurrentIcao · BodyWeightLbs(affects payload)
  WagePerDayCents(recurring) · WageLastBilledAt        // per-obligation watermark (§2.5)
  HealthMilli · Xp · ReputationMilli                    // per-employee reputation; the +0.028 gain lands here
  SkillsJson  // { flying, navigation, customer } skill bars
  Availability: StaffAvailability  // Available=1 | OnMission=2 | Resting=3   (separate from lifecycle Status)
  Status(Active|Fired)             // lifecycle; soft-delete via IsDeleted, never hard-fire the money trail
  AssignedAircraftInstanceId? · AssignedBaseId? · StandingOrderId? · HiredAt

StaffOffer                   (P3, hire market; server-ready)
  Id · Name · Role · HomeIcao · BodyWeightLbs · WagePerDayCents · SkillsJson · AvailableAtIcao · ExpiresAt

StandingOrder                (P3; the automation engine — standing orders + auto-fill + auto-refuel/repair)
  Id · CompanyId · ScopeType(Staff|Aircraft|Base) · ScopeId · Enabled
  AcceptMissionMask: int · MaxDistanceNm? · MinRewardCents? · MaxTurnaroundHours?
  AutoFill · AutoRefuel · AutoRepair · ReturnToBase · Priority
  // AutoRefuel offline: buys fuel at the per-airport FuelPrice (§6.10), snapshotting the unit price, with a
  //   DedupeKey so a reopen cannot double-buy. Autonomous execution reserves the airframe+pilot (§4.4/Staff
  //   Availability), writes a Flight (IsAutonomous=true) + ledger rows → offline progression == "left it running".

Base                         (P4, syncable)
  Id · CompanyId · AirportIcao · Name · Kind(Homebase|Outstation)
  DailyRentCents · RentLastBilledAt · StaffCapacity · ServicesMask(Refuel|Repair|Storage)
  ManagerStaffId?(P5) · EstablishedAt

Route                        (P4, syncable)
  Id · CompanyId · Name · OriginIcao · DestIcao · DistanceNm
  PreferredAircraftTypeId? · PreferredMission? · StandingOrderId? · Active
  // Route/custom jobs are still economy-priced (IPriceProvider); the player sets constraints, not the reward.

LoanTierDef                  (P4, reference/content)     // larger principal → lower APR (self-documenting)
  Tier: int (PK) · MinPrincipalCents · MaxPrincipalCents · AprBps

Loan                         (P4, syncable)              // liability tracked separately from cash
  Id · CompanyId · Tier · PrincipalCents · AprBps(snapshot at draw-down) · TakenAt · TermDays
  OutstandingCents · NextPaymentCents(shown up front) · NextPaymentDueAt · PaymentLastBilledAt
  Status(Active|PaidOff|Defaulted)
  // draw-down posts LoanPrincipal(credit); each payment posts LoanInterest + LoanPayment(principal)

InsurancePolicy              (P4, syncable)              // insurance is a POLICY + CLAIM path, not just a premium
  Id · CompanyId · ScopeType(Aircraft|Fleet) · ScopeId? · CoverageMilli(fraction of hull value)
  DeductibleCents · PremiumPerWeekCents · PremiumLastBilledAt · Active
  // premium posts InsurancePremium(debit, recurring); a covered total loss posts InsuranceClaim(credit) (§6.11)

CampaignDef / CampaignChapterDef   (P5 content)          // story chains: chapters + airport/surface/ILS/lighting/stars
CampaignProgress                   (P5 save)
AchievementDef / PilotAchievement  (P5 content + save)

SettingsProfile              (P5, save metadata)
  Id · SchemaVersion · FailureIntensityMilli · EnabledEventMask · MapType · VoicePack?
  AircraftScanOverridesJson  // { typeId: include/exclude } — an OVERRIDE on the scanner, NOT a manual list
  LastBackupAt?
```
Deliberately **not** modelled: the incumbent's fragmentation into separately-purchased "Pilots"/"Airline" modules + email-key registration. One coherent product; an entitlement concept is a Phase-5 open question (§11), not baked into the domain.

---

## 5. `LedgerEntry` — the spine (single source of truth for cash)

Current shape (`long Id`, `PilotId`, `At`, `Category`, `AmountCents`, `Description`, `RelatedEntityId`) is close but foreclosing on four axes: it attributes a row to at most one untyped entity, it welds money to the player pilot with a **cascade delete**, its `long` identity can't be merged or replayed, and the service posts **one row per commit** so a multi-row settlement isn't atomic. Target shape:

```
LedgerEntry
  Id: long                   // LOCAL running-balance order key + tiebreak for equal timestamps (keep autoincrement)
  EntryUid: Guid             // NEW — global identity for sync + idempotent replay
  Sequence: long?            // NEW/reserved-null — server-assigned authoritative order (P4)
  AccountId: Guid            // was PilotId — now the Company/account
  At: DateTimeOffset
  Category: LedgerCategory   // stored as STRING
  AmountCents: long          // + credit, − debit
  Description: string        // human-legible, itemised
  // typed attribution — a single settlement row rolls into SEVERAL P&L views at once:
  AircraftInstanceId: Guid?  // per-aircraft P&L    = Σ WHERE AircraftInstanceId = X
  StaffId: Guid?             // enables the pilot-vs-staff money filter (null = player-flown)
  BaseId: Guid?              // per-base/per-hub P&L
  // generic single-ref drill-down (job / loan / trade-lot / claim):
  RelatedEntityType: LedgerRefType?   // pinned-value enum: Job=1, Loan=2, StockLot=3, CheckFlight=4,
                                      //   Campaign=5, InsuranceClaim=6, Rental=7, Fuel=8, …
  RelatedEntityId: string?   // holds a Guid string; paired with the type
  DedupeKey: string?         // idempotency for recurring/offline postings; UNIQUE scoped to (AccountId, DedupeKey)
```

**Why typed attribution columns and not one polymorphic ref:** one flight-payout row legitimately belongs to the *aircraft flown* **and** the *staff pilot who flew it* **and** the *departure base* simultaneously. A single `RelatedEntityId` can express at most one, and because the ledger is append-only, attribution not captured at write time is *unrecoverable* — every asset's early financial life becomes a permanent blind spot. The nullable typed columns make each rollup a plain indexed `WHERE` with no join and no double-counting.

**Identity, ordering & idempotency (critic #13):**
- `EntryUid: Guid` is the global identity and the sync/replay key.
- **Running balance orders by `(At, Sequence ?? Id)`** — not the bare local autoincrement `Id`. Locally `Sequence` is null so it degrades to `(At, Id)`; after a backup merge or server sync, local `Id` ordering is meaningless and `Sequence` (server-assigned) carries the authoritative order. Reserving this now costs nothing.
- **`DedupeKey` uniqueness is scoped to `(AccountId, DedupeKey)`**, never a bare global unique index — a key like `wage:staff123:2026-08-11` omits account/origin and could false-collide across merged clients. Fold `OriginClientId` into the scope if a server ever assigns keys.

**Atomicity (critic #2).** `LedgerService` gains a **batch/transactional post** — `PostBatchAsync(accountId, lines[], applySideEffects)` — that writes all entries plus the settlement side-effects (`Flight.PayoutBreakdownJson`, XP, condition hits) in **one** `SaveChangesAsync`/transaction, asserting `Σ lines == breakdown.netCents` *inside* the transaction. The current single-row `PostAsync` remains for one-off movements. This must land before the 1f settlement slice because it shapes the service surface.

**Cache encapsulation (critic #11).** `Company.CashCents` moves from a public setter to a private/internal one mutated only by `LedgerService` (EF maps the private setter via a backing field; the service applies the delta and bumps `Version` in the same transaction). C# access modifiers cannot *fully* prove "only LedgerService writes this," so the backstop is a **runtime self-heal + assert** (open question #8, now resolved: assert-after-every-op in tests; a periodic reconcile-and-heal at runtime that logs any drift). No public code path can move money without a ledger row.

**`LedgerCategory` (string-backed; grows per phase).** Current members stay; because storage is a string, splits/additions below are safe with no value-shift corruption and no front-loading.

| Phase | Members (target taxonomy) |
|---|---|
| P1 | `StartingBalance`, `JobPayout`, `JobBonus`, `Penalty`, `AirportFee`, `Fuel`, `Repair`, `Transfer`, `Adjustment` |
| P2 | `AircraftPurchase`, `AircraftSale`, `RentalDeposit`, `RentalDepositRefund`, `RentalOwningDay`, `RentalFlightHour`, `ParkingFee`, `CheckFlightFee`, `Tip` |
| P3 | `StaffWage`, `StaffMissionPayout`, `StaffMovement`, `StaffHiringFee` |
| P4 | `AircraftMovement`, `BaseRent`, `BaseSetup`, `GoodsBuy`, `GoodsSell`, `LoanPrincipal`, `LoanInterest`, `LoanPayment`, `InsurancePremium`, `InsuranceClaim`, `RentalLossFee` |
| P5 | `CampaignReward` |

Notes: `AirportFee` and `JobBonus` are **not** in the current enum and are added for the P1 itemised settlement. `Fuel` already exists but is economically undefined today — it means **consumable tank fuel bought to fly** (§6.10), never the tradeable "fuel" commodity (which uses `GoodsBuy`/`GoodsSell`). `Insurance` is split into `InsurancePremium` (recurring debit) + `InsuranceClaim` (credit); `RentalLossFee` is the debit when a *rented* airframe is destroyed (§6.11). Coarse legacy members (`AircraftRental`, `Trade`, single `StaffWage`) are superseded by the finer forms as features land.

**Invariants:**
- `Company.CashCents == Σ LedgerEntry.AmountCents WHERE AccountId = Company.Id` — enforced only through `LedgerService`.
- `Flight.PayoutCents == Σ AmountCents WHERE RelatedEntityType=Flight-linked rows == PayoutBreakdownJson.netCents`. Settlement writes breakdown and ledger rows in **one transaction** (batch post above).
- Relationship is **`Restrict`, never `Cascade`** — deleting an owner must never nuke the source-of-truth table; owner existence is enforced in `LedgerService`, and everything soft-deletes.

Indexes: keep `(AccountId, At)`; add `(AircraftInstanceId, At)`, `(StaffId, At)`, `(BaseId, At)`, `(RelatedEntityType, RelatedEntityId)`, and a **unique** index on `(AccountId, DedupeKey)`.

---

## 6. Economy model (tunable parameters)

All numbers below are **illustrative defaults in "cr"** and live in a versioned `EconomyConfig`. No numeric literals in economy code; a lint/test asserts economy assemblies reference `EconomyConfig`, not inline constants.

### 6.1 Job reward & XP (legible, additive)
```
payload      = (pax job) ? paxCount : weightLbs
work         = w_call + w_dist*distanceNm + w_payload(type)*payload*distanceNm   // ton-/pax-miles
QuotedReward = round( work * M_type(type) * M_urgency(slack) * M_demand(lane), rewardStepCr )
loadFactor   = payload / usefulCapacity(aircraft)
QuotedXp     = round( (x_base + x_dist*distanceNm + x_load*loadFactor)
                      * X_type(type) * X_difficulty(runwayFt, elevationFt, night, weather) )
```
Landing quality, on-time, realism bonuses, penalties, fees, fuel, and the reputation premium are **settlement-side, not in the quote**. XP is decoupled from cr. Rank/qualification act as a **filter** (jobs shown *locked with the reason*, never hidden), never as a reward multiplier. **Custom/company/route jobs are priced by this same engine — the reward is never player-set** (critic #14).

*Illustrative mission table (`M_type` / `X_type` / rep base / typical gate):* Cargo 1.00 / 1.00 / 0.03 / A · Express 1.25 / 1.10 / 0.04 / tight deadline · Passenger 1.30 / 1.05 / 0.05 / C · Tourist 1.15 / 1.20 / 0.06 · Sensitive 1.55 / 1.25 / 0.07 / C+low-G · Hazardous 1.80 / 1.35 / 0.08 / D · Emergency/SAR 1.70 / 1.45 / 0.12 / rep+rank · Parachute 1.40 / 1.30 / 0.05 · Advertising 1.10 / 1.15 / 0.03 · Illicit 2.20 / 1.10 / **−0.15** / rep-gated.

### 6.2 Aircraft pricing (buy), factors shown on hover
```
BuyPrice = round( BaseHullValue(type)
                  * f_condition(hull%, engine%, airframeHours)
                  * f_regionalDemand(region, category)     // server-ready demand hook
                  * f_airportSize(runwayFt, class)          // small field → scarcity premium
                  * f_volatility(seed=(aircraftId, day)),   // bounded seeded walk 0.90–1.10
                  priceStepCr )
```
Each factor renders literally as a ±% ("Base 480,000 · Condition −8% · Regional +5% · Small-field +6% · Market −2% = 501,000"). `f_volatility` is *seeded*, reproducible, and a server can own the seed.

### 6.3 Rental, deposit-as-asset & rent-vs-buy break-even (shown before commitment)
```
Deposit = BuyPrice*depositPct   DayRate = BuyPrice*dayRatePct   HourRate = BuyPrice*hourRatePct
DaysToBreakEven ≈ (BuyPrice − ExpectedResidual) / (DayRate + HourRate*hoursPerDay + fuelPerDay)
```
Deposit posts a held `RentalDeposit` (cash out, excluded from P&L income) — but the held deposit is a **recoverable asset** and appears in the net-worth `recoverableDeposits` term until refund (critic #14). Return reconciles damage against it (`RentalDepositRefund` for the remainder, `Repair` for damage beyond). Returning at a different airport → `AircraftMovement` (ferry) fee. The pre-commit break-even preview shows **projected end-of-rental cost** (fees + any ferry) alongside cr/day and cr/hour. Defaults: `depositPct=10%`, `dayRatePct=0.12%/day`, `hourRatePct=0.90%/hr`. End-of-term handling is a scheduled biller transition — see §6.6.

### 6.4 Tiered loans (APR falls with principal; offline-catch-up aware)
Default tiers (principal / APR / weekly payment % of original): ≤25k / 22% / 4.0% · 25k–100k / 16% / 3.5% · 100k–400k / 11% / 3.0% · 400k–1.5M / 8.5% / 2.5% · >1.5M / 7% / 2.0%. Weekly compounding advanced to wall-clock `now` from the loan's own `PaymentLastBilledAt`; **interest realises in the ledger only when paid** (`LoanInterest`), while `Loan.OutstandingCents` carries the liability. An unaffordable week rolls the shortfall (with compounded interest) into `Outstanding` with a `missedPaymentSurcharge` and dings reputation. Early payoff any time, `prepaymentPenalty=0` (QoL). Borrowing limit = `f(reputation, netWorth)` where reputation is the operating-entity's (§6.7).

### 6.5 Trade (spatial arbitrage; separate Buy/Sell books; spoilage is an asset loss)
```
localIndex = basePrice(good)*f_region(good,region)*f_supplyDemand(seed=(airport,good,window))
BuyPrice(here)  = localIndex*(1+buySpread)      // you pay the ask
SellPrice(here) = localIndex*(1−sellSpread)     // you get the bid
UnitProfit(A→B) = SellPrice(B) − BuyPrice(A) − transportCostPerUnit
```
The bid/ask spread guarantees a same-airport round trip is never profitable (a reconciliation test). Held goods live in `Stock` with a cost basis so trade P&L is exact. **Perishable / undelivered loss is a `Stock` asset disposal — soft-delete the lot, net worth drops via the assets term — and posts ZERO ledger rows** (critic #9): the cash already left as `GoodsBuy`, so writing a `GoodsSell` "write-off" would double-count the loss and violate the cash-only invariant. `GoodsSell` is reserved strictly for actual cash-in sales. Reputation-gated goods (gold, artifact) require `MinReputationMilli`.

### 6.6 Recurring costs, fuel & offline billing (per-obligation, idempotent)
A single `IRecurringBiller` hosted service advances **each obligation from its own `LastBilledAt`** to `IClock.UtcNow` on tick and on load — never one shared watermark (critic #3) — posting due entries. Each charge is deterministic from `(anchor, period, elapsed)` and idempotent via a scoped `DedupeKey`. Where an obligation carries no explicit `LastBilledAt`, the documented, tested fallback is reconstruction from `anchor` + `DedupeKey`-existence checks. Wall-clock charges: rental day-rate + flight-hour (per `AircraftInstance.RentalLastBilledAt`), base rent (`Base.RentLastBilledAt`), staff wages (`Staff.WageLastBilledAt`), owned-aircraft parking (per airport, via `AirportServiceFee`), insurance premium (`InsurancePolicy.PremiumLastBilledAt`), loan payments (`Loan.PaymentLastBilledAt`).

**Two things are explicitly not wall-clock recurring charges:**
- **Hours-based maintenance** advances off `AircraftInstance.AirframeHours` vs `MaintenanceHoursWatermark`, bumped by *flights*, not elapsed time — closing the sim does not age the engine.
- **Rental expiry** is a scheduled *state transition* (critic #8): on catch-up, if the sim was closed past `RentalUntil`, the biller processes the return **at** `RentalUntil` — post `RentalDepositRefund`/`Repair`, `AircraftMovement` if off-origin, any late fee — idempotently via `DedupeKey`, and flips `Availability`/`Ownership`. The projected end-of-rental cost is shown pre-commit (§6.3).

**Fuel (critic #1).** Consumable tank fuel is a first-class cost. A refuel debits `Fuel` at the per-airport `FuelPrice` for the airframe's `FuelType`, **snapshotting the unit price** onto the ledger row. `StandingOrder.AutoRefuel` does this during offline catch-up with a `DedupeKey` so a reopen cannot double-buy. Fuel is included in the recurring/offline cost model and the break-even projection (§6.3). Fuel-the-consumable and fuel-the-commodity are **two systems that share a name**: the consumable is priced by `FuelPrice` and never sold back; the commodity is a `Stock` good traded via `GoodsBuy`/`GoodsSell`.

**Airport fees (critic #10).** Non-job arrivals, owned-aircraft parking, and autonomous flights draw their fees from `AirportServiceFee` (per-airport, provider-supplied, snapshotted at charge time) — never a local formula. Job destination fees remain frozen on the `Job`/`JobAssignment` quote.

Before any recurring commitment the UI shows projected cr/day, cr/week, and break-even flights/revenue — from the *same* projection service the biller uses, now inclusive of fuel and end-of-rental costs, so preview and reality match. **No in-game module/subscription fees.**

### 6.7 Reputation (0.000–100.000, stored ×1000; every effect bound to a named owner)
```
gain  = repBase(type) * qualityFactor * (1 − rep/100)      // asymptotes toward 100
loss  = incidentPenalty(kind)                              // easier to lose than gain
decay = decayPerIdleDay toward repFloor                    // default OFF
```
**Whose reputation each effect reads (critic #5):**
- **Player-flown** jobs read/adjust `Pilot.ReputationMilli`.
- **Staff-flown** autonomous jobs read/adjust that `Staff.ReputationMilli` (this is where the observed +0.028 lands) and scale that member's mission quality/payout; staff reputation never gates goods.
- **Gates & premiums** — goods locks (`Commodity.MinReputationMilli`), mission-type/lane unlocks, credit limit, job reward premium (→+8% @100), fee/rate discount (→−10%), loan APR shave (→−2 pts) — read the **operating entity's** reputation: the `Company` if an airline exists, else the `Pilot` (precedence rule, documented so the same number is never referenced ambiguously).
Applied as a *policy* over local outcomes, so a server could later publish a global effect schedule without code changes.

### 6.8 Parameter appendix (the explicit `EconomyConfig` tree)
`Jobs` (w_call, w_dist, w_payload[type], M_type/M_urgency/M_demand, rewardStep, x_*, X_type, X_difficulty) · `Landing` (band edges + reward Δ + star map) · `Exceedance` (basePenalty[kind], perEventCap, penaltyCapPct, durationScale, damage thresholds) · `AircraftMarket` (BaseHullValue[type], wCond, hoursPenalty, hoursRef, f_regionalDemand, f_airportSize, f_volatility, priceStep) · `Rental` (depositPct, dayRatePct, hourRatePct, relocationFeePct, lateFeePct, residualEstimatePct) · `Fuel` (basePricePerLb[fuelKind], f_region, refreshMinutes, reserveMarginPct) · `AirportFees` (landing/parking/handling by size+runway+elevation, refreshMinutes) · `Loans` (tier table, compoundPeriod, missedPaymentSurcharge, missedPaymentRepHit, borrowingLimit coeffs, prepaymentPenalty=0) · `Insurance` (premiumPerWeekPct, coverageMilli, deductiblePct) · `Trade` (basePrice[good], weight, volatility, minRep, perishTtl, buySpread, sellSpread, f_region, refreshMinutes, stockPerRefresh) · `Recurring` (baseRentByClass, wagePerDay[role], parkingPerDay, maintenanceHoursInterval, conditionDecayPerHour) · `Reputation` (repBase, qualityFactor weights, incidentPenalty, decay, floor, slopes, unlock thresholds, owner-precedence) · `TotalLoss` (autonomousCrashProb coeffs, hullWriteoffPolicy) · `Meta` (configVersion, currencyCode display-only, RNG salts).

### 6.9 Reconciliation invariants (the test suite that keeps it honest)
1. `Company.CashCents == Σ ledger`. 2. Per settlement, **written in one transaction**: `Σ breakdown lines == Σ ledger rows`, category-matched. 3. Loan: `Σ principal draws + Σ interest accrued == Σ (interest+principal paid) + outstanding` over life. 4. Rental: `Deposit == Refund + damageRepair` at return; a held deposit shows in net worth's `recoverableDeposits`. 5. Trade: same-airport instant round trip `UnitProfit ≤ 0` for every good. 6. **Spoilage / undelivered goods post ZERO ledger rows** (asset disposal only). 7. Offline equivalence: one 8-h jump == eight 1-h ticks (determinism), **per obligation** (a mid-window hire/loan bills correctly). 8. Fuel: a refuel debit equals `snapshotUnitPrice × lbs`; `AutoRefuel` is idempotent under reopen. 9. Total loss: rented-hull destruction leaves `deposit forfeited + RentalLossFee == owed`; owned-hull write-off moves no cash except a covered `InsuranceClaim`. 10. No-literals lint. 11. Enum-pinning test (every persisted enum's numeric mapping is fixed).

### 6.10 Fuel model (consumable vs commodity — two systems)
Summarised above; the design decision on record (critic #1): **consumable tank fuel** is priced per-airport by `FuelPrice` behind `IPriceProvider` (`GeneratedAt`/`ExpiresAt`, server-suppliable), selected by `AircraftType.FuelType`; a fill posts a `Fuel` debit snapshotting the unit price; `AutoRefuel` runs offline with a `DedupeKey`. **Commodity fuel** is an ordinary tradeable good (`Commodity.Code="fuel"`), bought/sold on the goods books, held as `Stock` with a cost basis. They never share a code path.

### 6.11 Total loss & insurance (crash / destruction; critic #7)
`Flight.Result = Crashed` (or a rental returned destroyed) triggers a money/asset path that was previously unmodelled:
- **Rented aircraft destroyed:** the held `RentalDeposit` is forfeited (simply not refunded) and the shortfall to the hull's value posts a `RentalLossFee` debit. Reputation hit applies.
- **Owned aircraft destroyed:** an **asset disposal** (soft-delete the `AircraftInstance`; net worth drops via the assets term) — this moves **no cash by itself**. If an `InsurancePolicy` covers it, a covered claim posts an `InsuranceClaim` credit of `min(coverageMilli×hullValue, …) − deductible`.
- **Insurance** is a policy entity with a recurring `InsurancePremium` debit and a claim path — not merely a premium line.
- **Offline autonomous crash:** an autonomous flight's crash probability is a bounded, *seeded* function of staff skill / aircraft condition / weather; a crash during catch-up is processed deterministically with the loss postings + reputation hit and surfaced in the reopen digest.

---

## 7. Scoring & flight events

The flight tracker is a state machine `Parked → Taxi → Takeoff → Climb → Cruise → Approach → Landing → Shutdown`, driven by `ISimTelemetrySource`. **Touchdown vertical speed is the headline recorded metric**, but the *whole* score is itemised and self-documenting — every signal surfaces as an expandable line so the player learns *why* they scored what they did.

**Landing bands (|fpm|; edges + reward Δ tunable):** ≤60 Greaser ★★★★★ +12% · 61–130 Smooth ★★★★ +6% · 131–240 Normal ★★★ 0% · 241–400 Firm ★★ −4% (small wear) · 401–600 Hard ★ −12% (hull wear + `Repair`) · >600 Severe ☆ −30% (damage, possible mission-fail).

**Exceedance penalties (each its own line; magnitude-scaled and capped):**
```
penalty_i = min(perEventCapCr, basePenalty(kind) * severity_i)
severity_i = 1 + overshootRatio + durationSeconds/durationScale
totalRewardPenalty = min(penaltyCapPct * QuotedReward, Σ penalty_i)     // cap = 60% (no double jeopardy)
```
Detectors: Overspeed (IAS>Vne) · Stall warning airborne · Over-G (large → hull damage + fail) · Flap overspeed (>Vfe) · Gear overspeed · Taxi >30 kt · Landing lights off during T/O·LDG · Missing payload match (forfeits realism bonus). Structural damage converts to a hull/engine condition hit and a **separate `Repair` line** that can push a leg net-negative; a **catastrophic** outcome escalates to `Result=Crashed` and the total-loss path (§6.11) rather than a mere `Repair`. Reward-side penalties stay capped; asset losses (repair, write-off) are billed honestly and separately.

**`PayoutBreakdownJson`** — the itemised settlement; every multiplier appears as its *delta* line so the column sums, each line maps 1:1 to a ledger row written in the same transaction:
```
{ "grossCents": …, "netCents": …,
  "lines": [ { "label": "Base reward",              "kind": "BaseReward", "amountCents": +… },
             { "label": "Landing bonus (Smooth, -95)","kind":"Bonus",     "amountCents": +… },
             { "label": "Realism payload bonus",     "kind": "Bonus",      "amountCents": +… },
             { "label": "Fuel (JetA, 640 lb @ …)",   "kind": "Fuel",       "amountCents": −… },
             { "label": "Destination fees",          "kind": "Fee",        "amountCents": −… },
             { "label": "Overspeed penalty",         "kind": "Penalty",    "amountCents": −… } ] }
```
*(Reward-side sub-lines may net into one `JobPayout` credit while fees net into one `AirportFee` debit; the JSON keeps every sub-line; `Σ lines == Σ ledger rows`.)*

**`EventsJson`** — the timestamped scored event log:
```
[ { "atOffsetMs": …, "phase": "Takeoff", "severity": "Warn",
    "code": "LandingLightsOff", "message": "Landing lights off during takeoff", "xpDelta": 0 },
  { "atOffsetMs": …, "phase": "Cruise",  "severity": "Success",
    "code": "PayloadMatch",     "message": "Payload matching — realism XP granted", "xpDelta": +5 } ]
```
`severity ∈ Info|Warn|Success`; `phase` matches the tracker state machine.

**Worked sanity check (220 nm Cargo, 1,800 lb, useful 3,000, rep 40.0, −95 fpm, on-time, payload matched, small field):** `work = 250 + 6.0·220 + 0.0016·1800·220 = 2,203.6` → Quoted **2,205**. Settlement +132 (Smooth) +150 (on-time) +100 (realism) +52 (rep +2%) = reward **2,639**; fees −220 → **net +2,419**. Ledger writes `JobPayout +2,639`, `AirportFee −220`; breakdown JSON sums to 2,419 == ledger 2,419. ✔ (When fuel is billed at settlement it is one more debit line reconciled the same way.)

---

## 8. UX divergences (mechanic → our approach)

Mechanics are the only borrowed layer; presentation is wholly ours. Each row: an observed genre usability failure → the behaviour we build instead.

| Genre failure | Our behaviour | Model hooks |
|---|---|---|
| Hand-curated "installed aircraft" checklist (+ 2020/2024 toggle, destructive "reset") | **Zero manual entry** — scan the sim's package folders on first run and on demand; auditable; infer sim edition (one-click correction, never mandatory); **non-destructive re-scan** | `InstalledPackage` (local-only scan state); `SettingsProfile.AircraftScanOverridesJson` is an *override*, not a list |
| Cryptic market compatibility tag ("Source: 2024-S") | **Compute the verdict, state it plainly** — "You have this — flyable now" / "Not installed — needs [package]" / "Installed, wrong sim edition"; "flyable by me now" is a first-class filter | `MarketListing.IsFlyableByPlayer` = join `InstalledPackage` (local) + qualifications |
| Features split across separately-purchased modules + email/key registration wall | **One product, one ledger, one model**; companion is the same backend over LAN; **no account/key to start** | no entitlement entity; `Company` + single SQLite save |
| Raw ledger with no forward view | **Ledger stays truth; derive forward views** — recurring outflow rate (incl. fuel), cash runway, per-asset P&L, **break-even before any recurring commitment**, "losing $N/week" signal | typed attribution columns (§5); projection service shared with the biller |
| Manual staff job queues | **Standing orders, not per-job assignment** — set policy, auto-fill idle staff, **preview before it acts**, offline progression on wall-clock, itemised digest on reopen; single-occupancy enforced so nothing double-books | `StandingOrder`; `AircraftInstance.Availability`/`Staff.Availability`; `Flight.IsAutonomous` |
| Dense unlabeled mystery-icon rows | **Self-documenting rows** — plain-language explanation in place; progressive disclosure; icons always paired with words | `*Def.Description`; `PriceFactorsJson`; `PayoutBreakdownJson` |
| Unexplained locked goods | **Every lock states its gate and the path** ("Requires rep 30 — you're at 7.7; earn N more"), and which reputation it reads (yours vs the airline's) | `Commodity.MinReputationMilli`; owner-precedence (§6.7) |
| Opaque fuel/fee charges | **Fuel and airport fees are quoted, snapshotted, and itemised** — the pump price you saw is the price charged; no local mystery formula | `FuelPrice`, `AirportServiceFee` behind the provider seam |
| Single landing-fpm score | Keep fpm as the **headline**, but make the whole score **itemised and traceable** | `Flight.EventsJson` / `PayoutBreakdownJson` |
| Opaque qualification / check-flight commitment | Show **which installed aircraft it unlocks**, the **total cost** (test fee + travel), and the **pass criteria** *before* booking | `QualificationClassDef`, `TestLocation.FeeCents`, computed distance |

---

## 9. Design-divergence principles

1. **Mechanics are the only borrowed layer.** Every screen, label, colour, icon, term, and column is designed from scratch.
2. **Legible over dense.** Every decision-driving figure is on the surface; everything else is progressive disclosure.
3. **Self-documenting by default.** Help lives at the point of use. The 10-minute first flight needs no external reading.
4. **Decisions are previewed, never blind.** Any recurring or irreversible commitment shows full cost (including fuel and end-of-rental), break-even, and consequence *before* confirmation.
5. **One product, one model.** No module fragmentation, no separate companion codebase.
6. **The ledger is truth for cash; every money view is a derivation.** Assets/liabilities (debt, cost basis, residual, **recoverable deposits**) are their own entities; net worth is computed; asset-only losses post no ledger rows.
7. **Automation you can see and trust.** Standing orders, auto-fill, offline progression always show *intent before acting* and a *digest after*, and never double-book an airframe or pilot.
8. **No gate without a reason and a path.** Every lock states why, the next action, and *whose* reputation it reads. Gates are data-driven.
9. **No friction before value.** No registration/account/key before the first flight.
10. **Compatibility is computed, never encoded** — against the real installed scan, which is **machine-local** and never mistaken for shared type identity.
11. **Server-readiness is a design constraint at the seam.** Generation behind `IJobSource`/`IPriceProvider`; **fuel and airport fees are provider outputs, not local formulas**; frozen-on-commit quotes; dormant sync columns; machine-local state kept out of syncable rows.

**Net position:** we cannot beat the free bundled career mode on setup, native UI, guaranteed compatibility, or price. We beat it — and the incumbent add-on — on **depth, agency, and legibility**: auto-detected fleet, computed compatibility, forward-looking money (fuel and fees included), previewed commitments, transparent automation, self-documenting everything.

---

## 10. Server-readiness & sync hooks (no infra yet)

What the Phase-4 shared-world ADR needs, all present now as dormant shape:
- **Idempotent, globally-ordered money:** `LedgerEntry.EntryUid` (Guid) + `Sequence` (reserved server order) + `(AccountId, DedupeKey)` unique. Running balance orders by `(At, Sequence ?? Id)` so merged/synced rows stay ordered.
- **Read-mostly shared layer** maps cleanly onto `MarketPrice`, `MarketListing`, `FuelPrice`, `AirportServiceFee` (shared prices/demand/fees) while `AircraftInstance`, `Pilot`, `Flight`, `Staff`, and the ledger stay private — the "shared prices, private progression" split.
- **Machine-local state never syncs:** `InstalledPackage` (the aircraft scan) is keyed by `HostClientId` and carries no merge columns, so a companion phone or second PC never corrupts the shared catalog.
- **Generation provenance:** `GeneratedAt`/`ExpiresAt` on all generated content; `OriginClientId` reserved; `UpdatedAt` + `IsDeleted` everywhere for last-writer-wins / CRDT-style merge.
- **No local-only assumptions:** a job, price, fuel, or fee row looks identical whether local- or server-sourced.

---

## 11. Open questions (tracked, not blocking)

1. **Ledger ambition.** Is the *money ledger itself* ever server-authoritative, or read-mostly local? Cheap insurance (`EntryUid`) taken regardless; the authoritative-ledger commitment is a Phase-4 ADR.
2. **Multi-load jobs.** Schema allows one `Flight` to settle many `JobAssignment`s; decide whether P1–P2 keep 1:1 and relax at P4.
3. **Player pilot as a `Staff` row?** Kept separate (player has qualifications/campaign progress a hire never has); revisit only if crew logic duplicates.
4. **Reputation dimensionality.** Single scalar per owner + event log now; a per-mission-type or per-region dimension can be added to `ReputationEvent` without a break.
5. **Entitlement / modules.** Whether to store *any* module/registration concept — deliberately absent; a Phase-5 decision.
6. **Loan amortization storage.** `NextPayment*` snapshot (chosen) vs a full generated schedule table.
7. **Reputation precision headroom.** Thousandths covers the observed +0.028; revisit if finer than 0.001 appears.
8. **Balance-cache vs pure-query — RESOLVED.** Keep `Company.CashCents` as a cache with a concurrency token *and* the `Σ ledger` authority; setter is private/internal (LedgerService-only); reconciliation cadence is assert-after-every-op in tests + a periodic self-heal at runtime that logs drift.
9. **Insurance scope.** Whether insurance ships in P4 as modelled (policy + claim) or is deferred; the entity shape is reserved either way (critic #7).
10. **Fuel: one system or two — RESOLVED as two.** Consumable tank fuel (priced by `FuelPrice`) and the tradeable fuel commodity (goods books) are separate code paths that share only a display word (critic #1).
11. **Per-obligation watermark storage.** Explicit `LastBilledAt` per obligation (chosen) vs pure `anchor + DedupeKey` reconstruction; both tested, explicit fields preferred for O(1) catch-up (critic #3).

---

## 12. Critic issues — resolution ledger

Each of the fifteen review issues, resolved (folded into the design) or explicitly recorded as a tracked question.

| # | Sev | Issue | Disposition |
|---|---|---|---|
| 1 | high | Fuel under-modelled; overloaded consumable-vs-commodity; `AutoRefuel` offline against a local price | **Resolved.** `FuelPrice` behind `IPriceProvider` (§4.5, §6.10); refuel = `Fuel` debit snapshotting unit price; offline `AutoRefuel` idempotent via `DedupeKey`; fuel added to recurring model + break-even (§6.6); two-systems disambiguation on record (OQ#10). |
| 2 | high | `PostAsync` non-atomic → settlement invariant unenforceable | **Resolved.** `LedgerService.PostBatchAsync` writes N rows + side-effects in one transaction, asserting `Σ lines == Σ rows` (§5, commitment #10); lands before 1f. |
| 3 | high | One global watermark can't bill per-obligation anchors; hours-maintenance isn't wall-clock | **Resolved.** Per-obligation `LastBilledAt` on Staff/Loan/Base/rental (§4.8, §4.4); hours-maintenance off `AirframeHours` watermark; contract + fallback documented and tested (§2.5, §6.6). |
| 4 | high | Machine-local scan state welded to shared `AircraftType` identity | **Resolved.** Split into local-only `InstalledPackage` (§4.4); `AircraftType` is clean shared reference; flyability joins local + quals (§8, §10). |
| 5 | med | `ReputationEvent` can't attribute to Staff; gates don't say whose reputation | **Resolved.** `OwnerType` includes `Staff=3` (§4.3); each gate/effect bound to a named owner with Company-else-Pilot precedence (§2, §6.7). |
| 6 | med | No in-flight/reserved state → double-booking; ambiguous `LocationIcao` mid-flight | **Resolved.** `AircraftInstance.Availability` + `EnRouteToIcao` + `ReservedByAssignmentId`; `Staff.Availability`; single-occupancy enforced (§4.4, §4.8). |
| 7 | med | Total loss / crash / rented destruction / insurance claim unmodelled | **Resolved.** §6.11 total-loss path; `RentalLossFee`, `InsuranceClaim` categories; `InsurancePolicy` entity; deterministic offline crash (scope tracked OQ#9). |
| 8 | med | Rental end-of-term absent from offline biller | **Resolved.** Rental expiry as a scheduled biller transition (auto-return, deposit reconciliation, ferry/late fee), idempotent; projected end cost shown pre-commit (§6.3, §6.6). |
| 9 | med | Perishable "write-off" described as a ledger action → double-count | **Resolved.** Spoilage = `Stock` asset disposal, **zero ledger rows**; `GoodsSell` = actual cash-in only; reconciliation test #6 (§6.5, §6.9). |
| 10 | med | No general airport-fee provider for non-job fees | **Resolved.** `AirportServiceFee` behind the provider seam for parking / non-job / autonomous arrivals; job fees stay frozen on the quote (§4.5, §6.6). |
| 11 | med | Public cache setter = money-without-ledger vector; settlement could read live `Job` | **Resolved.** Private/internal `CashCents` setter (LedgerService-only) + runtime self-heal/assert; settlement reads frozen `JobAssignment` quote (§2.3, §5, OQ#8). |
| 12 | low | Pinned enum values not applied | **Resolved.** Explicit `= N` on every persisted non-flags enum (incl. current `PilotRank`), powers of two for `[Flags]`; pinning test (§3, invariant #11). |
| 13 | low | `DedupeKey` global-unique false-collides on merge; running balance ordered by local `Id` | **Resolved.** Unique scoped to `(AccountId, DedupeKey)`; running balance orders by `(At, Sequence ?? Id)` (§5, §10). |
| 14 | low | Custom-job reward exploitable; net worth omits paid deposits | **Resolved.** Custom/route rewards economy-priced, never player-set (§4.6, §6.1); `recoverableDeposits` term added to net worth (§2, §6.3, §6.9). |
| 15 | — | *(reviewer supplied fourteen numbered issues plus the low-severity pair split into 13+14; all fourteen dispositions above.)* | n/a |

---

## Appendix — files reviewed
`src/Callsign.Core/Domain/Pilot.cs`, `…/Domain/LedgerEntry.cs`, `…/Data/CallsignDbContext.cs`, `…/Economy/LedgerService.cs`, `…/Game/NewGameService.cs`, `…/Time/Clock.cs`; `tests/Callsign.Core.Tests/TestDb.cs`, `LedgerServiceTests.cs`, `NewGameServiceTests.cs`; `docs/adr/0001-stack.md`, `docs/phase-1-plan.md`, `README.md`; `competitive-landscape-addendum.md`. Confirmed: no `Migrations/` directory (schema via `EnsureCreated()` only); `LedgerCategory` has `Fuel`/`Repair` but not `AirportFee`/`JobBonus` and no pinned values; `Pilot.CashCents` has a public setter; ledger FK is `Cascade`; `PostAsync` commits one row at a time — the window for free schema change is now.