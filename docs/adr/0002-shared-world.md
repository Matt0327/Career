# ADR 0002 — Shared-world economy authority

- **Status:** Accepted
- **Date:** 2026-08-12
- **Deciders:** Project owner + engineering
- **Supersedes:** —

## Context

Phases 1–4 built a complete single-player career/economy: a flying loop, a company
(aircraft, staff, bases, passengers, trade), a pilot career (rank, ratings, the full
mission roster, reputation), and a balance sheet (loans, net worth, insurance,
routes). The competitive addendum keeps raising a **shared/social world** as a
possible direction, and Phase 4 forces the question that shapes everything after it:

> Does the economy's **authority** live on the client — each install is its own
> world — or on a **server** — a shared, authoritative world?

Two facts frame the choice:

1. **Everything is built client-authoritative.** One SQLite file per save; the
   ledger is the single source of truth for cash, summed locally; jobs and prices
   are generated locally behind `IJobSource` / `IPriceProvider` (and the mission /
   loan / trade catalogs). No server adjudicates anything.

2. **The design has been kept *server-ready* from day one**, at almost no cost:
   - Money moves **only** through `LedgerService` — nothing computes or mutates a
     balance any other way (brief §6, enforced by every phase since).
   - Every syncable aggregate carries dormant sync hooks
     (`UpdatedAt` / `IsDeleted` / `OriginClientId`) for last-writer-wins merge, soft
     delete, and provenance — populated on write, never yet read.
   - `LedgerEntry.EntryUid` is a globally-unique idempotency/merge key, and
     `Sequence` is reserved for a server-assigned global order (null locally). The
     money endpoints are already **idempotent** (ADR-era Idempotency-Key work) —
     the same property a sync/replay layer needs.

So the decision is **not** binary-now-or-never. It is: *commit to a server now, or
keep the option open?* The forces:

- A shared, authoritative economy is a large, **ongoing** burden — hosting,
  accounts, anti-cheat, latency, availability, security. It would dominate the
  schedule and outlive it.
- The single-player loop **is** the product today; the shared world is speculative
  demand.
- **Retrofitting** sync onto a design that never reserved for it is a schema-wide,
  money-path-wide migration — expensive and error-prone.
- The seams and hooks that keep the option open are **already in place** and cost
  ~nothing to keep.

## Decision

**The economy is local-authoritative (read-mostly). We do NOT build a shared-world
server in this project. We ratify the server-ready design so the option is preserved
for a future, separately-scoped effort.**

Concretely:

1. **Authority stays on the client.** The save's ledger is authoritative for its
   cash; assets and liabilities are local entities; net worth is computed locally.
   No server arbitrates the economy.

2. **Preserve the option** — already in place, ratified here as a contract the
   codebase must keep honouring:
   - Money moves **only** through `LedgerService`. A future server could become
     authoritative over the ledger without changing a single caller.
   - Every syncable aggregate implements `ISyncable`
     (`UpdatedAt` / `IsDeleted` / `OriginClientId`) for last-writer-wins merge +
     soft delete + provenance. Dormant until a sync layer exists.
   - `LedgerEntry.EntryUid` is the merge/idempotency key; `Sequence` is reserved for
     a server global order. Money endpoints are idempotent — the replay property a
     sync layer requires.
   - Content and pricing are **server-suppliable** seams (`IJobSource`,
     `IPriceProvider`, the catalogs); nothing assumes "generated locally."

3. **Guardrail.** A `SyncReadiness` test asserts these invariants hold, so future
   work can't silently foreclose the option (drop a sync hook, add a second way to
   move money, lose the merge-key indexes).

We explicitly do **not** build, now: a server, accounts, network sync, anti-cheat,
an authoritative pricing service, or any merge/CRDT implementation. **The hooks are
reserved, not wired.**

## Consequences

**Good**

- The product ships without a server dependency: offline-first, no accounts, no
  network. Each save is a single self-contained SQLite file — trivial to back up.
- The cost of keeping the option open is ~zero: the fields already exist, and the
  guardrail keeps them meaningful.
- A future shared world is an **additive layer** (a sync/authority service over the
  existing ledger + hooks), not a rewrite.

**Accepted costs**

- No social/shared features today.
- The dormant sync columns are a few bytes of overhead with no current payoff; if a
  shared world is *never* built, that overhead is wasted (judged negligible).
- This ADR fixes **last-writer-wins** as the *assumed* merge policy but does not
  design the merge. A real shared economy may need stronger conflict handling and an
  authoritative sequencer; that is deferred to the future effort. This ADR only
  **preserves** the option — it does not commit the mechanism.

## Alternatives considered

- **Server-authoritative now.** Rejected as premature: the product is single-player;
  a shared economy is a large, ongoing infrastructure + security commitment with no
  validated demand, and would dominate the remaining schedule.
- **Drop the sync readiness (pure single-player).** Rejected: removing the hooks
  saves a trivial amount today but turns a future shared world into a schema-wide
  retrofit plus a re-audit of every money path. The option is worth its near-zero
  cost.
- **Peer-to-peer sync (no central authority).** Rejected for an *economy*: no
  authority means no anti-cheat, and an economy needs an arbiter. If a shared world
  is ever built, it will be **server-authoritative** over the ledger.
