// Seed the Callsign aircraft-image index with openly-licensed photos from Wikimedia Commons.
//
// Run ONCE, locally, with your Supabase service_role key (it bypasses Row-Level Security so it can write
// approved images). Keep that key secret — never commit or share it; it is only used here, on your machine.
//
//   Windows PowerShell:
//     $env:SUPABASE_URL="https://ewmjygogceutvgalnlns.supabase.co"
//     $env:SUPABASE_SERVICE_KEY="<your service_role key from Supabase - Settings - API>"
//     node scripts/seed-aircraft-images.mjs
//
//   bash:
//     SUPABASE_URL=... SUPABASE_SERVICE_KEY=... node scripts/seed-aircraft-images.mjs
//
//   Or (cleaner) put them in a git-ignored .env in the repo root and just run:
//     node scripts/seed-aircraft-images.mjs
//   .env:
//     SUPABASE_URL=https://ewmjygogceutvgalnlns.supabase.co
//     SUPABASE_SERVICE_KEY=eyJ... (your service_role key)
//
// For each aircraft type it searches Commons for a FREELY-LICENSED photo (CC0 / public domain / CC BY /
// CC BY-SA — nothing else), downloads a ~1200px version, uploads it to the public 'aircraft-images' bucket,
// and inserts an APPROVED aircraft_images row with the license + author attribution. Re-runnable: it skips
// any type that already has an approved image. Requires Node 18+ (global fetch).

import { readFileSync, existsSync } from 'node:fs';

// Load a local .env (KEY=VALUE per line) if present, so your service_role key stays out of shell history.
// Looks in the current directory and the repo root. .env is git-ignored — never commit it.
for (const envPath of ['.env', new URL('../.env', import.meta.url).pathname]) {
  try {
    if (!existsSync(envPath)) continue;
    for (const line of readFileSync(envPath, 'utf8').split(/\r?\n/)) {
      const m = line.match(/^\s*([A-Za-z0-9_]+)\s*=\s*(.*?)\s*$/);
      if (m && !process.env[m[1]]) process.env[m[1]] = m[2].replace(/^["']|["']$/g, '');
    }
    break;
  } catch { /* a malformed .env is ignored; real env vars still work */ }
}

const SUPABASE_URL = process.env.SUPABASE_URL;
const SERVICE_KEY = process.env.SUPABASE_SERVICE_KEY;
if (!SUPABASE_URL || !SERVICE_KEY) {
  console.error('Set SUPABASE_URL and SUPABASE_SERVICE_KEY environment variables first.');
  process.exit(1);
}

// Aircraft key (must match AircraftType.Key, i.e. the ICAO type designator) -> Commons search term.
// Start with the curated MSFS-2024 default fleet; add more rows any time and re-run.
const FLEET = [
  ['C152', 'Cessna 152'],
  ['C172', 'Cessna 172'],
  ['DA40', 'Diamond DA40'],
  ['SR22', 'Cirrus SR22'],
  ['BE36', 'Beechcraft Bonanza A36'],
  ['BE58', 'Beechcraft Baron 58'],
  ['DA62', 'Diamond DA62'],
  ['C208', 'Cessna 208 Caravan'],
  ['TBM9', 'Daher TBM 930'],
  ['PC12', 'Pilatus PC-12'],
  ['B350', 'Beechcraft King Air 350'],
  ['C25C', 'Cessna Citation CJ4'],
  ['C68A', 'Cessna Citation Longitude'],
  ['A20N', 'Airbus A320neo'],
  ['B748', 'Boeing 747-8'],
  ['H125', 'Airbus Helicopters H125'],
];

// Only these licenses are acceptable. Anything else (fair use, non-free, unknown) is rejected.
const OK_LICENSE = /^(cc0|cc[- ]by([- ]sa)?[- ][0-9.]+|public domain)/i;

const AUTH = { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}` };
const UA = 'CallsignImageSeeder/1.0 (https://callsign.app; aircraft image index)';

async function alreadyApproved(key) {
  const r = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images?key=eq.${key}&status=eq.approved&select=id&limit=1`, { headers: AUTH });
  const rows = await r.json().catch(() => []);
  return Array.isArray(rows) && rows.length > 0;
}

function stripHtml(s) { return String(s || '').replace(/<[^>]*>/g, '').replace(/\s+/g, ' ').trim().slice(0, 200); }

async function findFreeImage(term) {
  const api = `https://commons.wikimedia.org/w/api.php?action=query&generator=search`
    + `&gsrsearch=${encodeURIComponent(term + ' aircraft')}&gsrnamespace=6&gsrlimit=12`
    + `&prop=imageinfo&iiprop=url|extmetadata&iiurlwidth=1200&format=json`;
  const r = await fetch(api, { headers: { 'User-Agent': UA } });
  const j = await r.json();
  const pages = j?.query?.pages ? Object.values(j.query.pages) : [];
  for (const p of pages) {
    const info = p.imageinfo?.[0];
    if (!info) continue;
    const original = info.url || '';
    if (!/\.(jpe?g|png)$/i.test(original)) continue;                 // photos only, no svg/gif/tif/pdf
    const license = stripHtml(info.extmetadata?.LicenseShortName?.value);
    if (!OK_LICENSE.test(license)) continue;                          // free licenses only
    const downloadUrl = info.thumburl || original;                   // prefer the ~1200px render
    const author = stripHtml(info.extmetadata?.Artist?.value) || 'Unknown';
    return { downloadUrl, license, author, sourceUrl: info.descriptionurl || '' };
  }
  return null;
}

async function seed(key, term) {
  if (await alreadyApproved(key)) { console.log(`${key.padEnd(5)} already has an image — skipping`); return; }
  const hit = await findFreeImage(term);
  if (!hit) { console.log(`${key.padEnd(5)} no freely-licensed image found for "${term}"`); return; }

  const img = await fetch(hit.downloadUrl, { headers: { 'User-Agent': UA } });
  if (!img.ok) { console.log(`${key.padEnd(5)} download failed (${img.status})`); return; }
  const bytes = Buffer.from(await img.arrayBuffer());
  const ext = /\.png($|\?)/i.test(hit.downloadUrl) ? 'png' : 'jpg';
  const contentType = ext === 'png' ? 'image/png' : 'image/jpeg';
  const storagePath = `${key}.${ext}`;

  const up = await fetch(`${SUPABASE_URL}/storage/v1/object/aircraft-images/${storagePath}`, {
    method: 'POST', headers: { ...AUTH, 'Content-Type': contentType, 'x-upsert': 'true' }, body: bytes,
  });
  if (!up.ok) { console.log(`${key.padEnd(5)} storage upload failed (${up.status}) ${await up.text()}`); return; }

  const row = {
    key, storage_path: storagePath, content_type: contentType,
    attribution: `${hit.author} · via Wikimedia Commons`,
    license: hit.license, source_url: hit.sourceUrl, status: 'approved',
  };
  const ins = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images`, {
    method: 'POST', headers: { ...AUTH, 'Content-Type': 'application/json', Prefer: 'return=minimal' },
    body: JSON.stringify(row),
  });
  if (!ins.ok) { console.log(`${key.padEnd(5)} row insert failed (${ins.status}) ${await ins.text()}`); return; }
  console.log(`${key.padEnd(5)} seeded — ${hit.license}, ${hit.author}`);
}

console.log(`Seeding ${FLEET.length} aircraft images into ${SUPABASE_URL} ...`);
for (const [key, term] of FLEET) {
  try { await seed(key, term); } catch (e) { console.log(`${key.padEnd(5)} error: ${e.message}`); }
}
console.log('Done. Open the Hangar in Callsign to see them.');
