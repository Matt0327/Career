# Callsign Cloud on Supabase + Vercel

The online backend is **Supabase-native**: Supabase provides Postgres, Auth, and Storage; the desktop app's
local Host talks to it directly. Vercel hosts the marketing/landing site (and later a public web
leaderboard). There is **no separate server to run** — Supabase is the backend.

This guide gets a live cloud stood up. You do steps 1–4 once; I wire the app to it (step 5) once you hand me
the two **public** values.

## 1. Create the Supabase project
- supabase.com → New project. Pick a region near you. Save the database password somewhere safe.
- Wait for it to finish provisioning (~2 min).

## 2. Apply the schema
- SQL Editor → New query → paste the contents of
  [`supabase/migrations/20260813120000_initial_schema.sql`](../supabase/migrations/20260813120000_initial_schema.sql)
  → **Run**. (Or, with the Supabase CLI: `supabase link` then `supabase db push`.)
- This creates the tables, security policies, the leaderboard function, and the two Storage buckets
  (`saves`, `aircraft-images`). It's safe to re-run on a fresh project.

## 3. Auth
- Authentication → Providers → **Email** is on by default — that's all we need to start.
- (Optional, later) turn on **Discord** / **Google** for one-click sign-in — the app will pick them up.
- (Optional) Authentication → turn **"Confirm email"** on for real launch; off is fine while testing.

## 4. Make yourself an admin (to moderate aircraft images)
- Sign up once from the app (or Authentication → Users → Add user).
- SQL Editor → run, with your own user id:
  ```sql
  update public.profiles set is_admin = true where id = '<your-user-id>';
  ```

## 5. Hand me the two public values
From **Project Settings → API**, copy:
- **Project URL** — e.g. `https://abcd1234.supabase.co`
- **anon / public** key — the long `eyJ…` string labelled *anon public*

Both are **safe to share and safe to ship in the app** — they only allow what the security policies above
permit. Paste them here and I'll wire the app's cloud config to your project.

> ⚠️ **Never share the `service_role` key.** It bypasses all security. It is not needed by the app and must
> never be shipped or pasted into chat. If it ever leaks, rotate it in Project Settings → API.

## 6. Vercel (landing page) — later
- Create a Vercel project pointed at this repo's `web/` folder (I'll scaffold it).
- No secrets needed for a static landing page; a future web leaderboard will use the same public anon key.

## What changes in the app
- The Host's cloud gateway swaps from calling the local C# server to calling Supabase (Auth for sign-in,
  Storage for save push/pull, PostgREST for the image index + leaderboards). The **UI does not change** — it
  still talks only to the local Host.
- Passwords are now handled entirely by Supabase Auth (hashing, reset, verification), so the app never sees
  or stores them.
- The standalone `Callsign.Server` project is retired once the app is repointed and verified.

## Cost
Supabase free tier covers early use comfortably (Postgres, 1 GB storage, 50k monthly active auth users).
Vercel's hobby tier hosts the landing page free. No metered map keys — the satellite map is keyless.
