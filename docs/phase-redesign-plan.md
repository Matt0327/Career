# Callsign UX Redesign — phase plan

Goal: make the app clear, logical, and newcomer-friendly (NeoFly-legible) **without touching the
economy, saves, or any feature**. Only layout, navigation, and flow change. Approved direction: the
"A Clearer Callsign" proposal (artifact 7b47174d) — four moves.

Constraint: I can't see rendered pixels in this sandbox, so every phase is **live-QA'd headlessly**
(boot the Host against a seeded career, drive the DOM) to verify structure + behavior, and the user
eyeballs the look on their rebuild. Commit per phase.

## R1 — Grouped navigation  ✅/⬜
Fifteen flat tabs → five labelled areas in the rail. No tab is removed; they're grouped under headers.
- **Fly:** Home · Jobs · Flight
- **Fleet:** Hangar · Bases
- **Company:** Operations · Airline · Finances · Trade · Clients
- **Career:** Campaigns · Awards · Logbook · Community
- **You:** Settings
Work: restructure `NavRail` to render grouped sections with small headers; keep icons; keep the
active-highlight + hover label. No routing/Tab-type change needed (same tab ids, just grouped).

## R2 — Focused Home (command center)
Rebuild `Dashboard` from a 10-card scroll into: (1) a **next-step coach** naming the single next
action for the current state; (2) a tight **KPI row** (cash, net worth, fleet, rank); (3) a **needs-
attention** card (reuse the existing ops-status nudges); (4) everything else — fleet, finances,
campaign, recent flights, activity — **folded** behind links / collapsed sections, not dumped inline.
Includes move 04 (the next-step logic is the coach's brain: empty-logbook → accept a job; job-accepted
→ fly it; landed → take another; etc.).

## R3 — One screen shape (summary → list → detail → map)
A shared layout so every operational screen reads the same. Apply in order, one commit each:
1. **Jobs** (already closest — summary strip ✓, table, side detail, map) → make it the reference.
2. **Hangar** → same four zones.
3. **Trade** → same.
4. **Operations (Ops)** → same.
Extract the common structure into a reusable `ScreenLayout`/CSS so the zones line up identically.

## R4 — Clear next step
Folded into R2's coach. Also: first-run emphasis until the core loop (accept → fly → paid) is done once.

## Notes
- Reuse existing pieces: `HeroStat`/`hero-stats` (summaries), the ops-status nudges (attention), the
  toast system (feedback), `SatelliteMap`/`LogbookMap` (the map zone).
- Headless QA recipe: seed a career via `POST /api/game/new`; **rebuild the Host before QA** (the
  `bin/Release/net10.0-windows` DLL goes stale); drive via the Browser pane's `javascript_tool` (DOM),
  screenshots don't composite here.
