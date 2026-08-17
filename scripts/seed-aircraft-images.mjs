// Seed the Callsign aircraft-image index with the BEST openly-licensed photo of each aircraft from
// Wikimedia Commons — full-aircraft shots (prefers in-flight / large / landscape), not cockpits or details.
//
// Run locally with your Supabase service_role key (it bypasses Row-Level Security to write approved images).
// Keep that key secret — never commit or share it. It is only used here, on your machine.
//
//   Put SUPABASE_URL and SUPABASE_SERVICE_KEY in a git-ignored .env in the repo root, then:
//     node scripts/seed-aircraft-images.mjs
//   (or pass them inline: SUPABASE_URL=... SUPABASE_SERVICE_KEY=... node scripts/seed-aircraft-images.mjs)
//
// It scores up to 40 candidates per type and picks the best whole-aircraft photo, uploads it to the public
// aircraft-images bucket, and inserts an APPROVED row with license + attribution. Re-running REPLACES the
// previously-seeded image for each type (community submissions are left untouched). Requires Node 18+.

import { readFileSync, existsSync } from 'node:fs';

// Load a local .env (KEY=VALUE per line) if present, so your service_role key stays out of shell history.
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
  console.error('Set SUPABASE_URL and SUPABASE_SERVICE_KEY (env vars or a .env in the repo root).');
  process.exit(1);
}

// Aircraft key (must match AircraftType.Key, i.e. the ICAO type designator) -> Commons search term.
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

const OK_LICENSE = /^(cc0|cc[- ]by([- ]sa)?[- ][0-9.]+|public domain)/i;
// Reject shots that are NOT a clean view of the whole aircraft.
const BAD = /(cockpit|interior|cabin|panel|instrument|avionic|glareshield|seat|engine|propeller|\bprop\b|close[\s-]?up|detail|diagram|drawing|schematic|blueprint|\d[\s-]?view|three[\s-]?view|silhouette|crash|wreck|accident|\bmodel\b|toy|patch|\blogo\b|emblem|insignia|\bmap\b|\bsign\b)/i;

const AUTH = { apikey: SERVICE_KEY, Authorization: `Bearer ${SERVICE_KEY}` };
const UA = 'CallsignImageSeeder/1.0 (https://callsign.app; aircraft image index)';
const stripHtml = (s) => String(s || '').replace(/<[^>]*>/g, '').replace(/\s+/g, ' ').trim().slice(0, 180);

// Score up to 40 candidates and return the best full-aircraft photo (or null).
async function findBestImage(term) {
  const api = `https://commons.wikimedia.org/w/api.php?action=query&generator=search`
    + `&gsrsearch=${encodeURIComponent(term)}&gsrnamespace=6&gsrlimit=40`
    + `&prop=imageinfo&iiprop=url|size|extmetadata&iiurlwidth=1280&format=json`;
  const r = await fetch(api, { headers: { 'User-Agent': UA } });
  const j = await r.json();
  const pages = j?.query?.pages ? Object.values(j.query.pages) : [];
  const scored = [];
  for (const p of pages) {
    const info = p.imageinfo && p.imageinfo[0];
    if (!info) continue;
    const title = p.title || '';
    if (!/\.(jpe?g|png)$/i.test(title) || BAD.test(title)) continue;      // photos, whole aircraft only
    const license = stripHtml(info.extmetadata?.LicenseShortName?.value);
    if (!OK_LICENSE.test(license)) continue;                              // free licenses only
    const w = info.width || 0, h = info.height || 0;
    if (w < 1100 || h < 700) continue;                                    // big enough to be a real photo
    const ar = w / h;
    if (ar < 1.15 || ar > 2.6) continue;                                  // landscape-ish, not a square/portrait crop
    let score = Math.min(w * h, 30_000_000) / 1_000_000;                  // resolution
    if (/\b(in[\s-]?flight|flying|airborne|takeoff|take[\s-]?off|climb|approach|landing|banking)\b/i.test(title)) score += 14;
    if (ar >= 1.4 && ar <= 2.1) score += 6;                               // ideal aircraft framing
    const assess = stripHtml(info.extmetadata?.Assessments?.value);
    if (/featured/i.test(assess)) score += 12; else if (/quality|valued/i.test(assess)) score += 8;
    scored.push({ score, info, title, license });
  }
  if (!scored.length) return null;
  scored.sort((a, b) => b.score - a.score);
  const best = scored[0];
  return {
    downloadUrl: best.info.thumburl || best.info.url || '',
    license: best.license,
    author: stripHtml(best.info.extmetadata?.Artist?.value) || 'Unknown',
    sourceUrl: best.info.descriptionurl || '',
    w: best.info.width, h: best.info.height,
  };
}

async function seed(key, term) {
  const hit = await findBestImage(term);
  if (!hit) { console.log(`${key.padEnd(5)} no good full-aircraft image found for "${term}"`); return; }

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

  // Replace the previously-seeded row for this key (submitted_by is null = seeded, not a community upload).
  await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images?key=eq.${key}&submitted_by=is.null`, { method: 'DELETE', headers: AUTH });
  const ins = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images`, {
    method: 'POST', headers: { ...AUTH, 'Content-Type': 'application/json', Prefer: 'return=minimal' },
    body: JSON.stringify({
      key, storage_path: storagePath, content_type: contentType,
      attribution: `${hit.author} · via Wikimedia Commons`, license: hit.license, source_url: hit.sourceUrl, status: 'approved',
    }),
  });
  if (!ins.ok) { console.log(`${key.padEnd(5)} row insert failed (${ins.status}) ${await ins.text()}`); return; }
  console.log(`${key.padEnd(5)} seeded — ${hit.w}x${hit.h} ${hit.license}, ${hit.author}`);
}

console.log(`Seeding ${FLEET.length} aircraft images into ${SUPABASE_URL} ...`);
for (const [key, term] of FLEET) {
  try { await seed(key, term); } catch (e) { console.log(`${key.padEnd(5)} error: ${e.message}`); }
}
console.log('Done. Open the Hangar in Callsign to see them.');
