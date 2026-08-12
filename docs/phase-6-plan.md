# Phase 6 plan — the premium pass

Grounded in a full walkthrough of the reference app (14 screens: Aircraft Market, Goods Market, Flight,
Hangar, Jobs, Freelance, Finance, Logs, Qualifications, Career, Airline, Assets/Staff, Campaigns, Store,
Settings). This is the concentrated effort to raise every Callsign screen to the [quality
bar](design-and-imagery.md), and it also ratifies the standing charter: every new feature ships to this bar.

## What the reference teaches — adopt, and elevate

**Patterns worth adopting** (they make a career sim feel alive):

- A **persistent left icon rail** for navigation, and a **context header** always showing location, cash,
  fuel/level, and alerts.
- **An image on every entity.** Aircraft get a per-row thumbnail *and* a large detail image; pilots get a
  portrait; missions/campaigns get an illustrated card; airports get a locator.
- **Maps almost everywhere** — market, jobs, flight, logbook, staff — as the spatial anchor.
- **Dense but scannable data**, carried by iconography (commodities, mission types), **circular gauges**
  for condition/fuel, and small **at-a-glance charts** (balance over time, landing-fpm trend).
- **Carousels of your fleet**, **entity detail panels**, and **status pills** everywhere.

**Where we go above it (this is the whole point):**

- The reference leans on **generic AI-stock photography** (pilot faces, campaign/mission ambiance). We do
  **not** match that — it's exactly the "looks AI-made" trap. Our ambiance is **generative + curated
  open-licensed**, and people are shown as **generated identity avatars** (a monogram on a generated
  gradient — classy and honest), never fake faces.
- The reference uses **Google satellite tiles**. We render **our own** maps from public-domain OurAirports
  data — original, offline, no tile-server or licensing debt.
- The reference is **flat cyan-on-charcoal** with cramped spacing in places. We choose a **deliberate,
  distinctive visual identity** with real hierarchy, air, and motion. (Identity direction is the one open
  decision below.)

## 6a — Design-system foundation

The distinctive identity + the reusable kit everything is rebuilt on:

- **Identity:** a chosen palette (see decision), a type pairing (a characterful display + a clean body + a
  mono for data — inlined, no CDN), a spacing/elevation/motion scale, and an original icon set.
- **Component kit:** context header, nav rail, card, data table, **gauge**, stat tile, **chart**, **map
  frame**, **entity-image frame** (with graceful fallback), badge/pill, buttons, form controls.

## 6b — Imagery pipeline (clean-room)

- **Aircraft images:** a loader that reads the player's **installed MSFS aircraft `thumbnail.jpg`** (local,
  per-user, never redistributed) → **original livery-tinted silhouette** fallback (extends the Phase 5c
  generated-art language).
- **Maps:** a **self-rendered map** component from OurAirports coordinates — route lines, base/destination
  markers, and a **live flight moving-map** driven by telemetry. No tiles.
- **Avatars:** generated identity avatars for pilots/staff (monogram + generated gradient).
- **Ambiance:** generative visuals (sky gradients, topographic/route art) plus a small, hand-picked
  open-licensed set — every bundled asset in an **asset manifest** (file · source · licence).

## 6c — Screen-by-screen re-craft (priority order)

1. **Dashboard** — the airline hero (emblem + livery + standing) over an ambient band, with at-a-glance stats.
2. **Hangar / Market / Flight** — aircraft imagery, condition gauges, the moving-map on Flight.
3. **Jobs / Bases** — destination maps + illustrated mission cards.
4. **Finances / Logbook** — the balance/P&L charts + refined data tables.
5. **Campaigns / Awards / Airline** — illustrated cards + the emblem system.

The nav rail + context header land in 6a and apply to all of them.

## Constraints carried in

Clean-room (original / licensed only + asset manifest). Self-contained single-file, offline-first.
Performance — imagery optimised + lazy, never makes the app feel heavy. Accessibility and legibility never
traded for spectacle. "Deep but easy" throughout: dense, but with hierarchy and progressive disclosure.

## Open decision — the visual identity

The one thing to lock before building: **which distinctive direction** the design system commits to. Three
premium, clean-room, deliberately-not-generic candidates are on the table (Glass-cockpit / Aviation-editorial
/ Operations-center) — decided with the user, then a visual mock proves it before any screen is rebuilt.
