# Phase 15 — the big playtest feedback batch (2026-08-26)

Captured verbatim-ish so nothing is lost. Grouped by area + priority. Check items off as done.

## A. Flight loop (HIGHEST — blocks playtesting; several are re-reports)
- [ ] **cs-xxx tail numbers show instead of the aircraft NAME** in several places (re-reported).
- [ ] **Tab-switch loses everything**: returning to Flight keeps the job but the live view/info is gone.
      Want the Flight tab to *keep playing/rendering* regardless of tab.
- [ ] **Flight log**: phases go by way too fast; **remove climb/cruise/descent spam**; research + perfect
      EVERY message ("meldingen"). Best-of-the-best.
- [ ] **"Waiting for simulator" spam** in the flight log — retry silently in the background, don't log it.
- [ ] **Comfort score always ~0** even on a smooth flight — bug.
- [ ] **Readiness reading is wrong**: "Load ROBIN DR400-140B in MSFS (sim has 1000 Passengers)" — weird;
      should read the sim title correctly, not "1000 Passengers".
- [ ] **400 on Fly tab / app doesn't see the right plane**; and it still needs a manual "begin flight"
      instead of auto-starting when you roll.
- [ ] **Still must click "begin flight"** — if not auto-started, **warn every 1 min** to click "begin job".
- [ ] **Finish sequence (user's NEW spec, REVERSES the dwell-complete)**: land at the RIGHT airfield → set
      parking brake → engine off → THEN finish + rewards. (Implies the SimVar READ must be correct — the
      earlier "brake stuck green / engine-off not seen" was a read bug, not a reason to drop the requirement.)
- [ ] **Map plane icon "tweaks out"/spaces in all directions when not loaded** (not spawning everywhere now,
      but jitters). **Trail line** not always correct — starts mid-flight or not at all (pause not detected —
      the app should SEE the sim paused).
- [ ] **Required load = ACTUAL in-game weight loading** (NeoFly-style: read the sim's real payload/weight and
      require it matches the job) — NOT the in-app confirm button I built. Show sim weight like NeoFly.
- [ ] Start a job w/ the right plane, **end flight in MSFS + switch plane → job still flyable** (bug).

## B. Economy (DEEP DIVE requested)
- [ ] **Whole-app economy research + deep dive**: where every stat is computed, sent, shown, visualized;
      is it correct end-to-end. Same for a **system + statistics check** (data flow, is it displayed, correct).
- [ ] **Rewards/XP mostly by DISTANCE (nm)**, then payload + passengers — make it logical.
- [ ] **Max borrow fits level/reputation** (e.g. Trainee cap ~$50k).
- [ ] **Market**: does it update for where the USER is, or where they FLEW to? (verify + fix.)
- [ ] **Hired-pilot legs update the CLIENT too** (logical) — but with LOWER impression + reward vs flying it
      yourself.
- [ ] **Engine went to 0 after ONE Robin flight** — far too punishing; make wear flexible. **Service price
      scales with how damaged** hull/engine is. **Service hull / engine separately or both.**
- [ ] **Remove LEASE** (rent vs lease doesn't make sense — take lease out).
- [ ] **Not every aircraft rentable** — logical decision (GA/light yes; heavy/exotic/expensive no).
- [ ] **Leasing total ~40% MORE than new price** (if lease stays anywhere) — but see "remove lease".

## C. Aircraft / hangar / market
- [ ] **Images**: none of the ones I sent are updates EXCEPT the DR400 (which had none). Remove the old ones
      from the ones I sent and replace with the good ones.
- [ ] **More filters** at the aircraft market.
- [ ] **Hangar: assign a pilot per plane** (hired pilots get their own plane; user picks their own).
- [ ] **Add DCDesigns Concorde** to allowed-aircraft list + market @ $500M, NOT rentable, NOT leasable.

## D. Jobs
- [ ] **Jobs tab shows jobs my plane can't do** — don't offer un-doable jobs (or clearly gate).
- [ ] **Cancel a job** = tiny reputation hit to the client. (verify — cancelJob exists; make sure hit is tiny.)
- [ ] **Jobs map**: show a dot for **where the user/crew is now**, and on job-click draw a **line from there
      to the destination** (currently only the dest dot).
- [ ] **Fly-tab standing jobs multi-select rule**: 1 job if different destinations; up to 2 jobs to the SAME
      destination if they fit the chosen plane's useful load / pax.

## E. Fly-tab crew selection
- [ ] **Pick crew member OR yourself at the top of the Fly tab** — choose who flies a specific job.
- [ ] **Hired pilot finishes a job → auto-end + notification** (no manual "recall").
- [ ] **Pay to move the USER to the aircraft's location** too (currently only pay to bring the plane to you).

## F. Bases + Staff
- [ ] **Bases tab + system WAY better + better looking.**
- [ ] **Full research lap on the Staff tab + automation.**

## G. Content
- [ ] **Way more achievements.**
- [ ] **More icons everywhere** where not overwhelming (esp. Trade/market).

## H. Discuss / brainstorm (need the user)
- [ ] **AIRLINE should come WAY later** (unusual to have an airline the moment you start) and be **insanely
      complicated / big / special.** Gate creation far later + brainstorm the deep design.
- [ ] **Can we load OBJECTS / LINES into MSFS itself?** (important for future plans) — technical answer.

## Notes / conflicts to reconcile
- Finish-sequence + load-requirement REVERSE my Phase-13 changes (dwell-complete, in-app load confirm). The
  through-line: the SimVar READS were wrong; fix the reads, then the strict requirements are correct.
- "Airline instantly available" = the Phase 11 incorporation gate is too easy / too early. Raise the gate a lot.
