# Callsign — Online: vision & architecture (v1 draft)

**Decision: build B — the online product.** The offline core (Phases 1–6) is complete and excellent; this
is the plan to put a **backend under it** and clear the bar the incumbents (NeoFly et al.) set, then pass it.
The backend is the **foundation**, not a "someday Phase 7".

## Why a backend is the whole game

Every feature that makes the leading career addon feel premium — satellite maps, an image for every
aircraft, your career on any PC, a living shared economy, community events — is an **online** feature. They
are not polish on a local app; they are things a local app *cannot do*. The incumbent's edge isn't better
code, it's that it has a server. Building ours is the single highest-leverage move; everything else hangs
off it.

## The system, end to end

**Local-authoritative, server-synced** — the app stays fully playable offline; the cloud enriches, never
gates. This is exactly the posture [ADR-0002](adr/0002-shared-world.md) chose on purpose, so the pivot is
*additive*, not a rewrite.

- **Client (your PC)** — Callsign Desktop (the Sector UI, React + WebView2), reading the sim (aircraft,
  SimConnect telemetry), backed by **local SQLite** (offline-first save + ledger + cache). Syncs when online.
- **Callsign Cloud — API** — **ASP.NET Core** (same language as the app; reuse the domain), REST + realtime
  (SignalR). Holds: **auth**, the **sync engine** (last-writer-wins over the reserved `ISyncable` hooks +
  `EntryUid` merge keys), the **shared economy**, the **image index**, and a **tile proxy** (holds the map
  key server-side so it's never shipped to clients).
- **Data & services** — **PostgreSQL** (accounts, shared-world state, image index, events, leaderboards),
  **object storage + CDN** (images/liveries/art), **Redis** (presence, rate-limits, hot market state), and
  a **map provider** (satellite tiles — metered, see cost).

## The four things the backend unlocks

1. **Real satellite maps** — the tile proxy holds the provider key, so true satellite imagery everywhere
   without leaking a key anyone could extract and bill you for. Optional **bring-your-own-key** mode for
   power users (as the incumbent's Settings hinted).
2. **An image for every aircraft** — a hosted, **licensed + community-moderated** image index served by
   type; the *clean* version of a scraped DB (submissions with rights, reviewed). Local installed
   thumbnails still win.
3. **Accounts / your career anywhere** — sign in and your company follows you across PCs; the sync engine
   reconciles local ↔ cloud. Automatic backups. The ledger stays the source of truth for money.
4. **A living shared economy** — server-mediated shared markets, events, shared jobs, leaderboards. Money
   integrity enforced server-side (validate every posting against the ledger; merge keys make cheat-resistance
   tractable). ADR-0002 finally built.

## We already built the foundation right (not a teardown)

- The **ledger** is the single source of truth for cash — server authority validates against it.
- Every syncable aggregate carries dormant **sync hooks** (`UpdatedAt`/`IsDeleted`/`OriginClientId`) + a
  global `EntryUid` merge key — reserved and guard-tested.
- **ADR-0002** chose local-authoritative deliberately to keep this door open.
- Content is already **server-suppliable** (catalogs are data the server can hand down).
- The API is **ASP.NET Core in C#** — the backend is the same stack; the domain moves server-side largely
  as-is.

## The honest part — cost, ops & risk

Rough monthly to start (hundreds of users): compute + Postgres **$15–40**, object storage + CDN **$5–30**,
domain/email/monitoring **$10–25**, and **satellite tiles = the wildcard** (metered per ~1k tile loads; free
tiers exist, heavy use runs into the hundreds).

- **The map cost trap** — tiles are the one line that surprises you. Cache hard, offer BYO-key, cap the
  budget. Never a naked key in the client.
- **Ops & moderation are real, ongoing jobs** — uptime, backups, patches, and moderating community image
  uploads. You *run* an online product, you don't just ship it.
- **Anti-cheat is mandatory** — a shared economy is a cheating magnet; the server never trusts the client.
- **Legal & privacy** — accounts = user data (privacy/GDPR, secure auth); map providers have Terms; bundled
  images need a licence manifest.

**The one question that funds it all:** free (you absorb it) / donations / one-time unlock / small
subscription for the online tier? The offline app can stay free forever; the *cloud* is what has a running
cost. Choose on purpose, early.

## Build order — each rung shippable

- **B0 — backend skeleton + accounts.** API, auth, Postgres; the client signs in and does cloud
  save/restore. *Outcome: your career backed up, restorable on any PC.*
- **B1 — satellite maps.** Tile proxy + BYO-key; real imagery across jobs, bases, a live moving-map.
- **B2 — aircraft image index.** Hosted, licensed, moderated images served by type + a submission/review
  flow. *An image for (nearly) every plane, cleanly.*
- **B3 — the shared world.** Server-authoritative shared markets + events on the reserved sync hooks;
  anti-cheat enforced. ADR-0002 realised.
- **B4 — community & social.** Shared jobs, events, leaderboards, profiles.

## Stack (reuses what exists)

ASP.NET Core API (C#) · PostgreSQL · Redis · SignalR · object storage + CDN · ASP.NET Identity / OAuth ·
MapTiler / Mapbox / Google tiles · Fly.io / Hetzner / Railway · EF Core (shared domain). The desktop client,
Sector UI, domain model and ledger all stay; we add a server that speaks the same language.
