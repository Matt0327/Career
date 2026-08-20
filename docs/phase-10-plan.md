# Phase 10 — Mastery

## 0. Why this document exists

Phase 7 wired *consequence* into recorded state; Phase 8 gave the world weather, time, an economy, clients and certificates; Phase 9 deepened the *sim-integration layer* (real telemetry, live weather) while keeping it fun (the Fun Dial, L9). **Phase 10 closes the loop from *doing* to *getting better*** — it takes everything the flight already records (the 9c landing grade, 9a coaching nudges, 9d/9e events, the whole scored assessment) and turns it into **feedback the pilot can act on**, plus the last two flight-quality dimensions the moat still lacks (precision-approach and passenger comfort). The thesis from the Flight Plan holds: our un-gameable telemetry scoring is an edge nobody else has — Phase 10 is where that data *teaches*, not just *bills*.

## 1. The design laws (carried forward)

L1–L10 stand. Two get special weight here:

- **L9 — The Fun Dial** governs the debrief's *voice*: reward mastery first (lead with what went well), coach mistakes as "here's how", and only name a real consequence where one was actually billed. A debrief must never read as a scolding — it's a good instructor, not a nag.
- **L1 / L7 — we score the sim, we never command it.** The debrief and every new score read *recorded telemetry*; nothing claims to have changed the flight. New signals stay defaulted (L10) so a manual/legacy flight degrades to "not scored".

## 2. The systems

### 10a. The post-flight coaching debrief  *(build first — pure, verifiable, no new sim signal)*
A pure `FlightDebrief` that distils a settled `Flight` (its 9c scores + the persisted `FlightEvent`s incl. 9a coaching) into a structured `DebriefReport`: an overall grade + one-line headline, a **Strengths** list (what you flew well — reward mastery), and a prioritised **To improve** list (each note a *what happened → how to fix it*, promoting the recorded coaching/warning events and synthesising landing/approach coaching from the scores the raw events don't cover — e.g. a firm-but-not-warned 320 fpm touchdown). Surfaced on the logbook flight-detail. Zero economic effect; it only *explains* what already happened. This is the payoff for every scored signal Phases 7–9 recorded.

### 10b. Precision-approach scoring
Grade the approach against the runway's own geometry — lateral + vertical deviation from the extended centreline / a nominal 3° path below the gate — from the telemetry already streamed, folded into the existing approach score (defaulted so a field without the geometry scores exactly as today). Deepens the un-gameable moat; feeds 10a directly.

### 10c. Passenger-comfort loop
For passenger/VIP/tourist missions, a comfort score computed from the *recorded* bank/g/vertical-accel envelope (the 7d comfort read, extended) — smooth flying earns a comfort bonus + client loyalty, a rough ride costs the bonus (never below base pay, L9). Reuses the 8d client + 7d mission machinery; no new economic law.

### 10d. Icing / hazard ops  *(carries the L8 "raise stakes, never hard-block" line)*
Read the sim's icing / structural-stress signals (defaulted, L10) as *coached* hazards that bite only when ignored and sustained — the Fun-Dial ladder applied to environmental hazard, feeding both the score and the debrief.

*(The 3D flight replay is a UI-heavy, unverifiable-here surface — deferred to a client slice; 10a delivers the coaching value without it.)*

## 3. Build order

**10a first** — it's pure Core logic over already-recorded state (fully testable, no sim), and it's the visible payoff that makes every prior scored signal *matter to the player*. Then 10b (precision approach) and 10c (comfort) each add a dimension that flows straight into 10a's debrief. 10d last (needs sim signals, L10-defaulted, untested-here like the SimConnect wiring).

## 4. Consciously deferred
The 3D post-flight replay (client/rendering slice), and the Phase 11/12 bets (airline-employment ladder, shared economy, live map, in-sim EFB). Hard constraint unchanged: Callsign is a PC add-on; console reach only ever via an in-sim WASM/EFB panel.
