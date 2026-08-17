-- Callsign Cloud — Supabase-native backend schema.
--
-- Apply this to a fresh Supabase project (SQL Editor → paste → Run, or `supabase db push`).
-- It defines the whole online backend: profiles, cloud-save metadata, the aircraft-image index, and
-- leaderboards — with Row-Level Security so the database itself guarantees a player can only touch their
-- own data. Auth is Supabase Auth (auth.users); large blobs (save files, images) live in Storage.
--
-- Design mirrors the C# server we proved locally (Callsign.Server), so the client contracts barely change.

-- ─────────────────────────────────────────────────────────────────────────────
-- Profiles: one public row per auth user (display name shown on leaderboards).
-- ─────────────────────────────────────────────────────────────────────────────
create table public.profiles (
  id           uuid primary key references auth.users (id) on delete cascade,
  display_name text not null default 'Pilot' check (char_length(display_name) between 2 and 40),
  is_admin     boolean not null default false,   -- moderates the image index (grant manually, see the guide)
  created_at   timestamptz not null default now()
);

-- Create a profile automatically when someone signs up. The display name comes from the sign-up metadata.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer set search_path = public
as $$
begin
  insert into public.profiles (id, display_name)
  values (new.id, coalesce(nullif(new.raw_user_meta_data ->> 'display_name', ''), 'Pilot'));
  return new;
end;
$$;

create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function public.handle_new_user();

-- Is the current caller an admin? Used by image-moderation policies.
create or replace function public.is_admin()
returns boolean
language sql stable
security definer set search_path = public
as $$
  select coalesce((select is_admin from public.profiles where id = auth.uid()), false);
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Cloud saves: metadata only. The save file lives in Storage at saves/{user_id}/save.db.
-- ─────────────────────────────────────────────────────────────────────────────
create table public.cloud_saves (
  user_id    uuid primary key references auth.users (id) on delete cascade,
  size_bytes bigint not null default 0,
  device     text,
  updated_at timestamptz not null default now()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- Aircraft image index: metadata; image bytes live in Storage bucket 'aircraft-images'.
-- Keyed by the stable AircraftType.Key (e.g. 'C172'). Clean-room: license + attribution are mandatory,
-- and only 'approved' rows are ever served.
-- ─────────────────────────────────────────────────────────────────────────────
create type public.image_status as enum ('pending', 'approved', 'rejected');

create table public.aircraft_images (
  id           uuid primary key default gen_random_uuid(),
  key          text not null,
  storage_path text not null,
  content_type text not null default 'image/jpeg',
  attribution  text not null check (char_length(attribution) > 0),
  license      text not null check (char_length(license) > 0),
  source_url   text,
  submitted_by uuid references auth.users (id) on delete set null,
  status       public.image_status not null default 'pending',
  sort_rank    int not null default 0,
  created_at   timestamptz not null default now()
);
create index aircraft_images_key_status on public.aircraft_images (key, status);

-- ─────────────────────────────────────────────────────────────────────────────
-- Leaderboards: one standing per player. Self-reported for now (the app clamps); authoritative
-- validation arrives with the shared economy. Display name is joined from profiles, never duplicated.
-- ─────────────────────────────────────────────────────────────────────────────
create table public.leaderboard_stats (
  user_id         uuid primary key references auth.users (id) on delete cascade,
  net_worth_cents bigint not null default 0 check (net_worth_cents >= 0),
  flights         int    not null default 0 check (flights >= 0),
  reputation_milli int   not null default 0,
  xp              bigint not null default 0 check (xp >= 0),
  rank_key        text,
  updated_at      timestamptz not null default now()
);

-- A ranked board by metric. Called from the client via PostgREST RPC (/rest/v1/rpc/leaderboard).
create or replace function public.leaderboard(board text, lim int default 100)
returns table (position bigint, user_id uuid, display_name text, value bigint, rank_key text)
language sql stable
as $$
  with scored as (
    select
      s.user_id, p.display_name, s.rank_key,
      case board
        when 'flights'    then s.flights::bigint
        when 'reputation' then s.reputation_milli::bigint
        when 'xp'         then s.xp
        else                   s.net_worth_cents
      end as value
    from public.leaderboard_stats s
    join public.profiles p on p.id = s.user_id
  )
  select row_number() over (order by value desc) as position, user_id, display_name, value, rank_key
  from scored
  order by value desc
  limit greatest(1, least(lim, 500));
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Row-Level Security. Nothing is readable/writable except through these policies.
-- ─────────────────────────────────────────────────────────────────────────────
alter table public.profiles          enable row level security;
alter table public.cloud_saves        enable row level security;
alter table public.aircraft_images    enable row level security;
alter table public.leaderboard_stats  enable row level security;

-- profiles: everyone may read (leaderboard names); you may edit only your own.
create policy "profiles readable"     on public.profiles         for select using (true);
create policy "update own profile"    on public.profiles         for update using (auth.uid() = id);

-- cloud_saves: strictly your own row.
create policy "own save select"       on public.cloud_saves      for select using (auth.uid() = user_id);
create policy "own save insert"       on public.cloud_saves      for insert with check (auth.uid() = user_id);
create policy "own save update"       on public.cloud_saves      for update using (auth.uid() = user_id);

-- aircraft_images: public sees approved; you see your own submissions; admins see all. Submit as pending
-- (yourself); only admins change status.
create policy "images visible"        on public.aircraft_images  for select
  using (status = 'approved' or auth.uid() = submitted_by or public.is_admin());
create policy "images submit"         on public.aircraft_images  for insert
  with check (auth.uid() = submitted_by and status = 'pending');
create policy "images moderate"       on public.aircraft_images  for update using (public.is_admin());

-- leaderboard_stats: public reads; you upsert only your own standing.
create policy "leaderboard readable"  on public.leaderboard_stats for select using (true);
create policy "own standing insert"   on public.leaderboard_stats for insert with check (auth.uid() = user_id);
create policy "own standing update"   on public.leaderboard_stats for update using (auth.uid() = user_id);

-- Table + function grants (RLS still governs which rows; these govern table-level access).
grant select on public.profiles to anon, authenticated;
grant update on public.profiles to authenticated;
grant select, insert, update on public.cloud_saves to authenticated;
grant select on public.aircraft_images to anon, authenticated;
grant insert, update on public.aircraft_images to authenticated;
grant select on public.leaderboard_stats to anon, authenticated;
grant insert, update on public.leaderboard_stats to authenticated;
grant execute on function public.leaderboard(text, int) to anon, authenticated;
grant execute on function public.is_admin() to anon, authenticated;

-- ─────────────────────────────────────────────────────────────────────────────
-- Storage buckets + policies. 'saves' is private (per-user folder); 'aircraft-images' is public-read.
-- ─────────────────────────────────────────────────────────────────────────────
insert into storage.buckets (id, name, public)
values ('saves', 'saves', false), ('aircraft-images', 'aircraft-images', true)
on conflict (id) do nothing;

-- saves: a user may only touch files under saves/{their uid}/...
create policy "own save files" on storage.objects for all
  using      (bucket_id = 'saves' and (storage.foldername(name))[1] = auth.uid()::text)
  with check (bucket_id = 'saves' and (storage.foldername(name))[1] = auth.uid()::text);

-- aircraft-images: anyone may read; any signed-in user may upload (the DB row governs moderation).
create policy "aircraft images public read" on storage.objects for select
  using (bucket_id = 'aircraft-images');
create policy "aircraft images upload" on storage.objects for insert
  with check (bucket_id = 'aircraft-images' and auth.role() = 'authenticated');
