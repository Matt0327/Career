# Phase 14 — The Living Airline

The airline management deep-dive. Turns the scheduled-passenger network (Phase 11f — a Route
variant with frozen economics) into a **living operation**: demand that breathes with your
reputation and the season, fares you set, rival carriers you compete with, reliability you earn,
and aircraft you work harder or spare — all surfaced in a rich Network Operations view.

## Laws it must keep

- **L11 deepen-don't-duplicate** — one Company, one ledger, one read-model. Scheduled routes stay a
  `Route` variant (`SeatCapacity != null`); no parallel entity, wallet, or settlement path.
- **Money-pump invariant** — scheduled-pax revenue is **structurally pump-free**: passengers pay a
  one-way fare, there is no counterparty you buy capacity from and resell, so no round-trip pump
  exists whatever the fare/demand. The load factor is hard-bounded, and scheduled demand **never
  touches the two-sided commodity market** (rep/season/fare are never `MarketService.Quote` args),
  so the weather+rep ≤ 2×spread guard is preserved byte-for-byte.
- **L10 read-the-sim / degrade-gracefully** — every new field defaulted so a plain cargo/charter
  route (and every pre-14 save) is byte-identical to before. Neutral fare = 1000.
- **L9 the Fun Dial** — reliability coaches, it doesn't punish; a bad-weather cancellation is not a
  penalty, it's the operation the world handed you.
- Real wall-clock time (`IClock`), so every periodic beat is continuous, never a slow calendar tick.

## Slices

- **14a — Living demand & yield management.** The seat load factor is computed LIVE each reconcile
  from your CURRENT operating reputation + a calendar season curve + fare elasticity, instead of the
  value frozen at creation. You set a **fare** on a scheduled route (like the cargo markup, but
  price-elastic: a higher fare thins the cabin — there's a revenue-maximizing sweet spot). Revenue
  per trip = seats × live load × frozen per-seat yield × fare. Pure `ScheduledDemand` model.
- **14b — Competition & market share.** Invented (clean-room) rival carriers hold a share of each
  route's market. Your share rides your fare-vs-market, your reputation, and your frequency; it
  scales the load factor. Rivals react slowly, so undercutting/out-reputing them wins share over time.
- **14c — Reliability & disruptions.** On-time performance from crew skill + weather; a share of
  scheduled departures cancel (weather) or run late (thin crew). Reliability is a tracked stat that
  feeds operating reputation (earned, not gamed) and is shown per route + network-wide.
- **14d — Fleet utilisation.** Block-hours/day per airframe, a utilisation read, and an
  over-utilisation wear multiplier — working a tail hard costs condition faster (already partly
  modelled via hours→wear; this surfaces + tunes it for the network).
- **14e — Network Operations dashboard.** The rich UI: the network at a glance, and per-route P&L
  (revenue, fuel, crew, fees, margin), load factor, fare, market share, reliability, utilisation.

Each slice: additive migration if any, pure-model unit tests, reconcile wiring, DTO + UI, a focused
review, Core/Host green, headless live-QA where the UI changes.
