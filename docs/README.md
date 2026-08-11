# Documentation

Docs live **in the repo, next to the code**, so they cannot drift out of date —
this is a hard requirement (brief §3.1): the app must explain itself, per-version,
in-app, with no browser required. The plan is to generate in-app contextual help
from these sources as the UI is built.

## Contents

- [`adr/`](adr/) — Architecture Decision Records. Start with
  [0001 — stack](adr/0001-stack.md).

## Planned (as features land)

- `help/` — per-screen contextual help content (source for the in-app help panel).
- `onboarding/` — the first-run guided walkthrough script (brief §3.1).
- `economy/` — the tunable economy model and its numbers (brief §9: designed from
  first principles, retunable via a spreadsheet).
