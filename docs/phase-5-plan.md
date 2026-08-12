# Phase 5 plan — identity, story & scale

Phases 1–4 built the loop, the company, the career, and the balance sheet. Phase 5 gives the company a
**name to live up to and a story to follow**: achievements that recognise how you play, authored campaigns
that string legs into arcs, an identity/reputation that reads at the scale of an operation, and the
settings/companion polish that makes a save feel owned. It's where a sandbox becomes a game with a shape.

The groundwork is already in place. Progress is legible from data the game keeps (flights, rank, reputation,
net worth, bases, routes) — so recognition needs no new bookkeeping, only a lens. Content is authored as
self-documenting, server-suppliable catalogs (the `MissionCatalog` / `LoanCatalog` pattern), and every
syncable aggregate still carries the dormant `ISyncable` hooks, so nothing here forecloses the shared-world
option ([ADR-0002](adr/0002-shared-world.md)).

## Build order — each step independently playable, finish before the next

1. ✅ **5a — Achievements.** A data-driven `AchievementCatalog` of milestones across flying, career,
   business and finance, each reading a metric off existing progress against a target (so a *locked* badge
   also yields a progress bar). Earned badges are persisted once — `AchievementAward`, EF migration
   `AddAchievements`, unique per (company, key) so awarding is idempotent — and evaluated **lazily on read**,
   so they catch up whenever you open the new **Awards** tab. No new events to wire, no economy touched.
   *Unit-tested:* first-flight awarded + frequent-flyer progress, idempotent re-award, butter-landing gate,
   stable earned-at.

2. ✅ **5b — Campaigns.** Authored story arcs — a `CampaignCatalog` of ordered, escalating goals with a
   cash reward paid **through the ledger** when the arc completes. A shared `ProgressMetricsService`
   (extracted from 5a) now backs both achievements and campaigns off one snapshot; `CampaignService`
   advances the current step lazily on read (a single pass can clear several) and pays the completion
   reward **exactly once** (`CampaignReward` category, `LedgerRefType.Campaign`, dedupe key). A Campaigns
   tab shows each arc's step checklist with progress bars. Two arcs ship: *First Contract* and *Build an
   Airline*. *Unit-tested:* step advance, reward-on-completion via the ledger, paid-once. (Flight-specific
   objectives — "fly this exact leg" — are a later extension; 5b's objectives read the shared snapshot.)

3. **5c — Identity & reputation at scale.** Company/airline identity as a first-class thing — a name, an
   original livery/emblem the player picks, and reputation that reads at the operation level, not just the
   pilot's. Feeds job access and campaign gating.

4. **5d — Settings & companion.** Preferences, plus the save **backup / export / restore** already shipped
   in the pre-release hardening pass — rounded out here into a proper settings home.

## Invariants (carried from Phases 1–4)

- The ledger stays the single source of truth for **cash**; achievements and campaigns never move money
  except through economy-computed payouts posted to the ledger.
- Recognition and story are **read models over existing state** wherever possible — earned, not stored twice.
- Reference/content (achievement and campaign definitions) is self-documenting and server-suppliable.
- Schema changes ship as EF migrations; everything stays server-ready (the `ISyncable` hooks stay reserved).
- All identity art is **original** — clean-room, no third-party assets.

## Explicitly deferred

An entitlement/registration concept remains an open question, deliberately not baked into the domain. A live
shared world stays a separate, future effort — the design is ratified (ADR-0002), not built.
