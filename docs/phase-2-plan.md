# Phase 2 plan — the company you run

Phase 1 shipped "a pilot who flies jobs and gets paid." Phase 2 turns that into **a company**:
you own aircraft, run them, hire people, open bases. The quality-of-life thesis holds throughout —
legible/itemised money (every movement a ledger row), break-even shown *before* a commitment, never
a manual aircraft list, automate the busywork. Everything stays **server-ready** (money only moves
through the ledger) so a future shared world (Phase 4 ADR) isn't foreclosed.

The data model is already sketched in [`domain-notes.md`](domain-notes.md) §4.4–4.8 and §6.2–6.7.
Build order — each step independently playable, finish before the next:

1. ✅ **2a — Aircraft ownership.** `AircraftInstance` (an owned airframe: tail, type, location,
   condition, hours). A buy market at your airport prices each type with an itemised "why this price".
   Buy → the airframe is yours and cash is debited via the ledger. "Your hangar" lists what you own.
2. ✅ **2b — Fly what you own.** A new career gets a gifted starter airframe; you pick which of your
   aircraft flies a leg; on landing at the destination the airframe (and the pilot) move there and it
   ticks airframe hours. The airframe binding is optional, so the synthetic/test path still works.
3. ✅ **2c — Running costs.** A landing/handling fee on every arrival (itemised in the payout); hull +
   engine condition that wears with hours and hard landings; maintenance you pay for to restore it and
   reset the interval — all itemised ledger rows.
4. ✅ **2d — Staff.** Hire pilots (economy-set wages); assign a pilot + an owned aircraft to a repeating
   route (reward frozen at economy price); trips + landing fees + wages are reconciled deterministically
   from elapsed wall-clock on reopen, all through the ledger. The airframe ticks hours/condition too.
5. ✅ **2e — Bases.** A free home base at career start; open more for a setup fee + recurring rent
   (billed in the reconcile pass); landing fees are waived at your own bases. Nearby airports are
   offered as priced base candidates.
6. ✅ **2f — Passenger charters.** A `Passenger` mission type generated alongside cargo (a
   `CompositeJobSource` mixes both on every board refresh); reward scales with heads × distance, the
   load reads in seats, and settlement's "right aircraft" bonus gates on seats ≥ pax. (Express/
   tourist/etc. remain future variants on the same rails.)
7. ✅ **2g — Trade.** A per-airport commodity market (deterministic prices that swing by place and
   re-roll each time window, buy above / sell below a mid) with `InventoryLot` holdings carrying a
   weighted-average cost basis. Buy low, the goods ride with your pilot (settlement moves lots to the
   destination), sell high elsewhere; purchases are capped by your fleet's carry capacity. Every
   movement is a `Trade` ledger posting.

## Pre-ship infrastructure (tracked, before any real save ships)

These came out of the 2a adversarial review and are deliberately scheduled, not skipped:

- ✅ **EF Core migrations.** The app now creates/upgrades its schema through EF migrations
  (`InitialCreate` baseline + `Database.Migrate()` on startup), so a shipped save survives a schema
  change across updates instead of being wiped. Tests keep `EnsureCreated()` on throwaway DBs. New
  migrations: `dotnet ef migrations add <Name> --project src/Callsign.Core` (design-time factory
  included).
- ✅ **Idempotent money endpoints.** Every money-committing endpoint (aircraft buy, maintenance, base
  open, trade buy **and sell**) accepts an `Idempotency-Key` header; the key drives the ledger dedupe,
  so a replayed request returns the original outcome instead of moving money twice. A sell replay
  rebuilds its realised breakdown from the (sale-invariant) lot cost basis. The web client sends a
  stable key per action and retries it on a network drop. (The UI already debounces; `Company.Version`
  stops the concurrent-race desync.)

## Invariants (carried from Phase 1, enforced at every step)

- Every money movement is a `LedgerEntry`; `Company.CashCents` is only a cache of the ledger.
- Prices are always economy-computed (never player-set), even for custom jobs — or it's an exploit.
- Reference/market data is server-suppliable; nothing assumes prices are generated locally.
- Reuse the quote-freeze pattern: obligations snapshot their terms so break-even stays honest.
