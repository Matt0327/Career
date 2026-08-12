# Phase 4 plan — the balance sheet (finance, risk & scale)

Phase 1 gave a flying loop; Phase 2 a company; Phase 3 a pilot's career. Phase 4 gives the company a
**balance sheet and a horizon**: borrow to grow, see your true net worth, insure the fleet against a bad
day, run **routes** instead of one-off jobs — and decide, in an ADR, whether the economy ever leaves this
one machine. It's where "how much cash do I have" becomes "what is this company actually worth."

The bones are already reserved. The ledger is the single source of truth for **cash**; assets (aircraft,
inventory) and liabilities are their own entities, so **net worth is a computed view, never stored**
(domain-notes §2). Every syncable aggregate already carries the dormant sync hooks
(`UpdatedAt`/`IsDeleted`/`OriginClientId`) for the shared-world question. `LedgerCategory` already has the
loan lines; the model for loans/insurance/routes is sketched in [`domain-notes.md`](domain-notes.md) §4.8.

## Build order — each step independently playable, finish before the next

1. ✅ **4a — Loans.** A `LoanCatalog` of tiers (larger principal → lower APR, self-documenting) + a `Loan`
   liability tracked **separately from cash** (EF migration `AddLoans`). Taking one credits cash via a
   `LoanPrincipal` entry; the offline reconcile pass now also bills declining-balance interest +
   straight-line principal (`LoanInterest` + `LoanPayment`) and marks a loan paid off at term; early payoff
   clears the balance now. A Finances tab shows debt, the borrow form (APR shown before you sign), and the
   tier table. *Verified live:* a $500k Business loan @10% drew down to cash and paid off cleanly; the
   reconcile amortisation is unit-tested.

2. ✅ **4b — Net worth & P&L.** `FinanceService` computes a balance sheet on read — cash (the ledger sum) +
   assets (each airframe's condition-adjusted resale value, inventory at cost) − liabilities (outstanding
   loan principal) — plus a cash-flow/P&L window that aggregates the ledger by category. Nothing is stored;
   no money moves. The Finances tab now leads with a net-worth breakdown and a cash-flow table.
   *Verified live:* net worth $1.34M (cash $799k + aircraft $740k + inventory − loans $200k), with the
   cash-flow view itemising StartingBalance / LoanPrincipal / AircraftPurchase / Trade.

3. ✅ **4c — Insurance.** An `InsurancePolicy` is a policy + claim path (EF migration `AddInsurancePolicies`):
   insuring an airframe sets a weekly premium (billed in the reconcile pass as `InsurancePremium`) for
   coverage of a fraction of its hull value; once the airframe is worn to the write-off threshold, a claim
   pays out the covered value minus the deductible (`InsuranceClaim`) and retires it. Premium + claim are
   ledger-tracked; the reopen digest gains an insurance line. A Finances-tab card shows policies (claim /
   cancel) and quotes to insure. *Verified live:* insured the starter at $2,101/wk (payout $472,680); a
   claim on the healthy airframe was refused with the reason. Payout + write-off are unit-tested.

4. ✅ **4d — Routes.** Named, scheduled lines between two of your bases (EF migration `AddRoutes`), flown by
   an owned aircraft + staff pilot with a chosen mission. The reward is economy-frozen at creation from the
   mission's economics; trips book autonomously in the reconcile pass and are **fee-free** (both ends are
   your bases — an incentive to grow the network). Reputation-gated / illicit missions can't be routed. A
   Routes card in the Staff tab opens/cancels them. *Verified live:* opened a Cargo route EHAM→EHSE at
   $1,523/trip; a route to a non-base was refused. Fee-free trip-booking is unit-tested.

5. ✅ **4e — Shared-world ADR.** [`docs/adr/0002-shared-world.md`](adr/0002-shared-world.md) records the
   decision: the economy stays **local-authoritative (read-mostly)**; we do **not** build a shared-world
   server, but we ratify the server-ready design so the option is preserved for a future, separately-scoped
   effort (money only through the ledger; `ISyncable` hooks reserved; `EntryUid` as the merge key; content
   behind server-suppliable seams). A `SyncReadiness` guardrail test asserts those invariants so future work
   can't silently foreclose the option. No server, no accounts, no network — the hooks are reserved, not wired.

## Invariants (carried from Phases 1–3, enforced at every step)

- The ledger is the single source of truth for **cash**; assets and liabilities are their own entities.
- **Net worth is computed, never stored** — cash + assets − liabilities, derived on read.
- Prices are always economy-computed (never player-set), including route and custom jobs.
- Reference/content (`LoanTierDef`, insurance terms) is self-documenting and server-suppliable.
- Schema changes ship as EF migrations; money endpoints stay idempotent; everything stays server-ready.

## Explicitly deferred (Phase 5)

Company/airline **identity** & reputation at scale, **campaigns** (authored story chains) + achievements,
and **settings/backup/companion** features → **Phase 5**. An entitlement/registration concept remains an
open question (§11), deliberately not baked into the domain.
