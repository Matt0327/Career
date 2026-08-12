# Design & imagery — the quality bar

This is the standard every screen is held to, retroactively and going forward. It exists because the
default "ship it plain, polish later" path produces software that looks generic and AI-made — exactly what
Callsign must **not** be.

## The bar

- **Premium, distinctive, best-in-class.** Not a templated card grid with a stock accent colour. A
  considered visual identity that could sit next to the best desktop flight-sim companions and hold its own.
- **Deep, but easy.** The systems underneath are genuinely complex (a real economy, a ledger, a career).
  The interface makes that depth *legible*, never simplified away — progressive disclosure, strong
  hierarchy, "summary first, detail on demand." Complexity earned, not dumbed down.
- **Image-forward.** Real imagery wherever it adds meaning — aircraft, airports, maps, liveries, weather —
  the way a mature career UI feels alive, not a spreadsheet with buttons.
- **Crafted, not generated.** Every view is designed on purpose. No screen looks like it was stamped out.

## The hard part: rich imagery **and** clean-room

Callsign's hard rule is clean-room — original or openly-licensed assets only, never a third party's art,
databases, or scraped content ([clean-room memory]). "Images everywhere, like the reference apps" has to be
reconciled with that. It is, through five sources, in priority order:

1. **Your own installed aircraft (local, per-user).** MSFS aircraft packages ship a `thumbnail.jpg`. We
   already scan the player's installed aircraft (`aircraft.cfg`); we can read that local thumbnail and show
   it in the Hangar, Market, and Flight views. It's the player's own content, shown to them on their own
   machine — **never bundled or redistributed**. This is the single biggest "real aircraft images" win, and
   it's fully clean. (Fallback to #2 when a thumbnail is absent.)
2. **Original, livery-tinted illustrations (we draw).** A set of original aircraft **silhouettes** by
   category (light single, twin, turboprop, jet, airliner, heli), tinted with the airline's accent so they
   read as *your* fleet. Consistent, on-brand, zero licensing risk. The emblem system (Phase 5c) is the
   first piece of this generated-art language.
3. **Self-rendered maps from open data.** We hold OurAirports coordinates + runways for ~85k airports
   (public domain). We render **our own** maps — route lines, base markers, a destination locator, a live
   moving-map on the flight HUD. Original rendering, offline, no tile-server dependency or attribution debt.
4. **Curated open-licensed ambiance.** A small, hand-picked set of genuinely license-free photography
   (Unsplash-license / public-domain / Wikimedia PD) for hero and atmosphere — skies, cockpits, airport
   aprons. Every bundled asset recorded in an **asset manifest** (file, source, licence) so the clean-room
   posture is auditable. Used sparingly, never for core data.
5. **Procedural / generated.** Emblems (done), generated liveries on the silhouettes, weather visuals — art
   the app composes at runtime from the player's own state.

**Rules for imagery:** optimise hard (WebP/AVIF, sized variants, lazy-load); keep the single-file exe lean
— prefer vector / generated / local over bundled photos; maintain the asset licence manifest; everything
degrades gracefully with no network and no thumbnail.

## Where imagery lands (screen → image)

| Screen | Imagery |
|---|---|
| Hangar / Market / Flight | Aircraft image — local sim thumbnail → original silhouette fallback |
| Dashboard | Airline hero: emblem + livery + standing, over an ambient band |
| Jobs / Bases | Destination locator maps + (where available) airport photo |
| Flight HUD | Live moving map + attitude, self-rendered from telemetry |
| Campaigns / Missions | Illustrated arc/mission cards |
| Routes | Network map of your base-to-base lines |

## How we execute

- **A dedicated Phase 6 — "Premium pass"**: (a) a real **design-system foundation** — a characterful
  display + clean body typeface (inlined), a palette chosen beyond the current indigo default, a spacing /
  elevation / motion system, reusable components; (b) the **imagery pipeline** — the local-thumbnail loader,
  the original silhouette set, and the map renderer; (c) **screen-by-screen** re-craft to the bar.
- **And a standing charter**: from now on, *every* new feature ships to this bar — craft and imagery
  included — not "add it plain, polish later." This doc is that charter.

## Non-negotiables carried in

Clean-room (original / licensed only, asset manifest). Single-file, offline-first. Performance — imagery
never makes the app feel heavy. Accessibility and legibility never traded for spectacle.
