# Phase 2 plan — the company you run

Phase 1 shipped "a pilot who flies jobs and gets paid." Phase 2 turns that into **a company**:
you own aircraft, run them, hire people, open bases. The quality-of-life thesis holds throughout —
legible/itemised money (every movement a ledger row), break-even shown *before* a commitment, never
a manual aircraft list, automate the busywork. Everything stays **server-ready** (money only moves
through the ledger) so a future shared world (Phase 4 ADR) isn't foreclosed.

The data model is already sketched in [`domain-notes.md`](domain-notes.md) §4.4–4.8 and §6.2–6.7.
Build order — each step independently playable, finish before the next:

1. ⏳ **2a — Aircraft ownership.** `AircraftInstance` (an owned airframe: tail, type, location,
   condition, hours). A buy market at your airport prices each type with an itemised "why this price".
   Buy → the airframe is yours and cash is debited via the ledger. "Your hangar" lists what you own.
2. **2b — Fly what you own.** Pick one of your aircraft for a job; the flight + settlement bind to
   that instance; the airframe moves to the destination and ticks airframe hours. (Retires the
   Phase-1 "fly by title" stand-in.)
3. **2c — Running costs.** Airframe hours → maintenance billing; hull/engine condition; parking &
   landing fees on arrival — all itemised ledger rows, all shown before you commit.
4. **2d — Staff.** Hire pilots; standing orders that fly routes autonomously while you're away
   (the "automate the busywork" promise), reconciled deterministically on reopen.
5. **2e — Bases.** Open/close bases; home-basing, parking, and where your fleet lives.
6. **2f — More mission types.** Passengers first (manifest + seats), then express/tourist/etc.
7. **2g — Trade.** Buy low / sell high across airports; goods as assets with a cost basis.

## Pre-ship infrastructure (tracked, before any real save ships)

These came out of the 2a adversarial review and are deliberately scheduled, not skipped:

- **EF Core migrations.** The app builds its schema with `EnsureCreated`, so a *new* save gets new
  tables/columns but an *existing* save does not. Replace with an `InitialCreate` baseline + a
  `Database.Migrate()` on startup before we ship a save anyone keeps. (Pre-release, dev DBs are
  disposable — delete `%LOCALAPPDATA%\Callsign\callsign.db` after a schema change.)
- **Idempotent purchases.** Give money-committing endpoints a client idempotency token so a network
  retry replays instead of double-charging (the UI already debounces; the `Company.Version`
  concurrency token now stops the concurrent-race desync).

## Invariants (carried from Phase 1, enforced at every step)

- Every money movement is a `LedgerEntry`; `Company.CashCents` is only a cache of the ledger.
- Prices are always economy-computed (never player-set), even for custom jobs — or it's an exploit.
- Reference/market data is server-suppliable; nothing assumes prices are generated locally.
- Reuse the quote-freeze pattern: obligations snapshot their terms so break-even stays honest.
