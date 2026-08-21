# Phase 12 — "Make it land": the path to the best single-player experience

*(From an honest 5-lens audit, 2026-08-21. You don't need to memorise any of this — it's here so we can just work through it one step at a time.)*

## The verdict

**Callsign is an ~85/100 machine delivering a ~65/100 experience. The gap is delivery and incentive — not depth.** The systems are all built (Phases 1–11); the game just doesn't *show* the player its best work, and doesn't give a reason to fly it by hand. Shared world stays off the table.

| Dimension | Grade | The real issue |
|---|---|---|
| Look / visual craft | 80 | Already premium — not the gap |
| Content / competitive | 63 | One hole: all mission types fly the same |
| Legibility / onboarding | 62 | Beautiful inputs, invisible payoff |
| Core-loop feel | 62 | The moat is computed but never reaches the player |
| Economy balance | 57 | Great machine, broken curve (pays you to *not* fly) |

## The plan — three waves

### Wave 1 — "make the moat land" — ✅ DONE (35eccb7, e4e92ca, aee680d, e75268a)
- [x] **Fix the lying ladder copy** — the career screen said shipped features "aren't switched on."
- [x] **Deliver the score + coaching debrief at settlement** — the instructor debrief now lands on the end-of-flight screen, not a tab away.
- [x] **Show the flywheel's receipt** — jobs show a "⌂ +$X" hub note; the detail shows "your reputation lifts this hub."
- [x] **Make overall score the headline metric** — logbook has a sortable Score column, avg/best-score stats, and a Flight-score trend.
- [x] **Make the calendar bite** — a small, bounded seasonal tilt on job demand; the season chip tooltip explains it.

*Delivered: the depth you shipped now shows up and is felt — the biggest win being the score + coaching at the moment you land.*

### The front door — ✅ DONE (02442dc, a3f47e6, 7974b90)
A player-requested addition on top of Wave 1 — the first thing a new player meets.
- [x] **A 5-step onboarding wizard** — account (create / sign in / Continue offline) → MSFS edition badge → pick your first aircraft (Cessna 152 / Robin DR400 / JMB VL3) → pilot (callsign + avatar + home ICAO) → a setup review, then Start flying with $10,000.
- [x] **Per-tab first-visit tutorials** — the first time you open any tab, a short guide card explains what it's for and what you can do there; remembered per device, reopenable with a `?` in the header. This directly answers the audit's #1 gap (legibility, 62).

*Live-QA'd end-to-end against a throwaway database: walked all five wizard steps and confirmed the career came out exactly as chosen, and drove the tutorials (first-visit pop, dismiss-and-remember, `?` reopen) in the running app.*

### Wave 2 — "fix the curve + feel pass" (I build, then we tune together — needs your eyes)
- [ ] **Rebalance so mastery pays**: charge fuel on autonomous trips (today crew flying is *cheaper* than flying yourself), soften crew perfection, add route-pay saturation, make top yields reachable only by hand-flying.
- [ ] Visual nits you'll spot in the running app: a light-mode amber that washes out, two overflowing tables, a weight-vs-MTOW readout on the Flight tab.

### Wave 3 — the one content move (later)
- [ ] **In-flight mission objectives** via the telemetry you already stream (Tourist = hold low over a POI, SAR = reach a box and loiter…) so 11 same-y types become 11 distinct *flights*. Only worth it once Waves 1–2 give a reason to hand-fly.

## What NOT to do
No new subsystems. No shared world. Don't over-polish visuals before fixing the curve. Don't nerf the empire into unfun (diminishing returns, not caps). Don't rebuild the plumbing — it's solid.
