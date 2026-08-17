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
  // GA singles
  ['C152', 'Cessna 152'], ['C172', 'Cessna 172'], ['DA40', 'Diamond DA40'], ['SR22', 'Cirrus SR22'],
  ['SR20', 'Cirrus SR20'], ['BE36', 'Beechcraft Bonanza A36'], ['P28A', 'Piper PA-28 Cherokee'],
  ['PA18', 'Piper PA-18 Super Cub'], ['C182', 'Cessna 182 Skylane'], ['C206', 'Cessna 206 Stationair'],
  ['M20P', 'Mooney M20'],
  // GA twins
  ['BE58', 'Beechcraft Baron 58'], ['DA62', 'Diamond DA62'], ['BE60', 'Beechcraft Duke'],
  ['BN2A', 'Britten-Norman Islander'],
  // turboprops
  ['C208', 'Cessna 208 Caravan'], ['TBM9', 'Daher TBM 930'], ['PC12', 'Pilatus PC-12'],
  ['B350', 'Beechcraft King Air 350'], ['BE20', 'Beechcraft King Air 200'],
  ['DHC6', 'De Havilland Canada DHC-6 Twin Otter'], ['PC6', 'Pilatus PC-6 Porter'],
  ['AT802', 'Air Tractor AT-802'],
  // business jets
  ['C25C', 'Cessna Citation CJ4'], ['C68A', 'Cessna Citation Longitude'], ['SF50', 'Cirrus Vision Jet'],
  ['E55P', 'Embraer Phenom 300'], ['LJ45', 'Learjet 45'], ['GLF6', 'Gulfstream G650'],
  ['CL60', 'Bombardier Challenger 650'],
  // airliners & regional
  ['A20N', 'Airbus A320neo'], ['A319', 'Airbus A319'], ['A21N', 'Airbus A321neo'],
  ['B738', 'Boeing 737-800'], ['B38M', 'Boeing 737 MAX 8'], ['B77W', 'Boeing 777-300ER'],
  ['B789', 'Boeing 787-9'], ['A359', 'Airbus A350-900'], ['A388', 'Airbus A380'],
  ['B748', 'Boeing 747-8'], ['E75L', 'Embraer E175'], ['CRJ7', 'Bombardier CRJ700'],
  ['AT76', 'ATR 72'], ['DH8D', 'Bombardier Dash 8 Q400'],
  // helicopters
  ['H125', 'Airbus Helicopters H125'], ['B06', 'Bell 206'], ['B407', 'Bell 407'],
  ['H145', 'Airbus Helicopters H145'], ['R44', 'Robinson R44'],
  // warbirds / military
  ['F117', 'Lockheed F-117 Nighthawk'], ['F22', 'F-22 Raptor'], ['SPIT', 'Supermarine Spitfire'],
  ['P51', 'North American P-51 Mustang'],
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

async function alreadyApproved(key) {
  const r = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images?key=eq.${key}&status=eq.approved&select=id&limit=1`, { headers: AUTH });
  const rows = await r.json().catch(() => []);
  return Array.isArray(rows) && rows.length > 0;
}

// Bootstrap the shared catalog with a set of {key, display_name} rows (upsert, keeps player reports).
async function upsertCatalog(rows) {
  try {
    await fetch(`${SUPABASE_URL}/rest/v1/aircraft_catalog?on_conflict=key`, {
      method: 'POST', headers: { ...AUTH, 'Content-Type': 'application/json', Prefer: 'resolution=merge-duplicates,return=minimal' },
      body: JSON.stringify(rows),
    });
  } catch { /* best-effort */ }
}

async function readCatalog() {
  try {
    const r = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_catalog?select=key,display_name`, { headers: AUTH });
    const rows = await r.json();
    return Array.isArray(rows) ? rows : [];
  } catch { return []; }
}

async function seed(key, term, refresh) {
  if (!refresh && await alreadyApproved(key)) { console.log(`${String(key).padEnd(5)} has an image — skip`); return; }
  const hit = await findBestImage(term);
  if (!hit) { console.log(`${String(key).padEnd(5)} no good full-aircraft image for "${term}"`); return; }

  const img = await fetch(hit.downloadUrl, { headers: { 'User-Agent': UA } });
  if (!img.ok) { console.log(`${String(key).padEnd(5)} download failed (${img.status})`); return; }
  const bytes = Buffer.from(await img.arrayBuffer());
  const ext = /\.png($|\?)/i.test(hit.downloadUrl) ? 'png' : 'jpg';
  const contentType = ext === 'png' ? 'image/png' : 'image/jpeg';
  const storagePath = `${key}.${ext}`;

  const up = await fetch(`${SUPABASE_URL}/storage/v1/object/aircraft-images/${storagePath}`, {
    method: 'POST', headers: { ...AUTH, 'Content-Type': contentType, 'x-upsert': 'true' }, body: bytes,
  });
  if (!up.ok) { console.log(`${String(key).padEnd(5)} upload failed (${up.status}) ${await up.text()}`); return; }

  if (refresh) await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images?key=eq.${key}&submitted_by=is.null`, { method: 'DELETE', headers: AUTH });
  const ins = await fetch(`${SUPABASE_URL}/rest/v1/aircraft_images`, {
    method: 'POST', headers: { ...AUTH, 'Content-Type': 'application/json', Prefer: 'return=minimal' },
    body: JSON.stringify({
      key, storage_path: storagePath, content_type: contentType,
      attribution: `${hit.author} · via Wikimedia Commons`, license: hit.license, source_url: hit.sourceUrl, status: 'approved',
    }),
  });
  if (!ins.ok) { console.log(`${String(key).padEnd(5)} insert failed (${ins.status}) ${await ins.text()}`); return; }
  console.log(`${String(key).padEnd(5)} seeded — ${hit.w}x${hit.h} ${hit.license}, ${hit.author}`);
}

const refresh = process.env.SEED_REFRESH === '1';
const fleetTerms = new Map(FLEET.map(([k, t]) => [k, t]));

// Seed the whole community catalog (built-in fleet + whatever players have reported). The built-in fleet is
// registered first so there's always content; player-reported types fill in over time.
await upsertCatalog(FLEET.map(([key, term]) => ({ key, display_name: term })));
const catalog = await readCatalog();
const list = catalog.length ? catalog : FLEET.map(([key, term]) => ({ key, display_name: term }));
console.log(`Catalog: ${list.length} aircraft. Seeding images${refresh ? ' (refresh all)' : ' (missing only)'} into ${SUPABASE_URL} ...`);
for (const { key, display_name } of list) {
  const term = fleetTerms.get(key) || display_name || key;
  try { await seed(key, term, refresh); } catch (e) { console.log(`${String(key).padEnd(5)} error: ${e.message}`); }
}
console.log('Done. Open the Hangar in Callsign to see them.');
