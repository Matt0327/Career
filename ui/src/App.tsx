import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import {
  api, money,
  type Achievement, type AircraftHistory, type AircraftOffer, type AirlineData, type Assignment, type BackupFile, type BaseOffer, type BaseView, type Campaign, type Challenge, type CareerHighlights, type CheckCentre, type CertificateStatus, type Client, type CheckFlightDone, type CloudSaveMeta, type CloudStatus, type Diverted,
  type FinancesData, type FinanceDetail, type AttributionLine, type CashPoint, type StatementRow, type FlightLog, type FlightDetail, type FlightTotals, type Insurance, type Inventory, type Job, type LeaderboardRow, type LedgerEntry, type LiveEvent, type Loan, type LoanOffer, type Loans,
  type MarketQuote, type OwnedAircraft, type QualClass, type RankTier, type ReconcileResult, type Reputation,
  type DispatchLeg, type RouteData, type Settled, type Staff, type StaffCandidate, type StandingOrder, type State, type Telemetry, type UsedListing, type VersionInfo, type Weather, type WorldState, type WsEvent,
  type RentalOffer, type ActiveRental, type ActiveLease,
} from './api'
import { loadPrefs, savePrefs, type Prefs, type Theme } from './prefs'
import * as L from 'leaflet'
import 'leaflet/dist/leaflet.css'

// ─── Toasts ──────────────────────────────────────────────────────────────────
// A single, always-visible notification stack (top-right, fixed) so the result of ANY action is seen
// without scrolling. `useToast()` returns a drop-in for the old per-tab `setMsg(text)` — pass a string to
// raise a toast, null to do nothing. Tone is inferred from the text (errors go red) unless given explicitly.
type ToastTone = 'info' | 'ok' | 'warn' | 'error'
interface ToastItem { id: number; text: string; tone: ToastTone }
let _toastId = 1
let _toasts: ToastItem[] = []
const _toastSubs = new Set<(t: ToastItem[]) => void>()
function _emitToasts() { for (const fn of _toastSubs) fn(_toasts) }
function dismissToast(id: number) { _toasts = _toasts.filter(t => t.id !== id); _emitToasts() }
function inferTone(text: string): ToastTone {
  return /(not enough|couldn'?t|can'?t|cannot|must |already|isn'?t|not rated|grounded|spoiled|over capacity|failed|invalid|unknown|unable|not found|exceeds|refus|no .+ (to|at|in your))/i.test(text) ? 'error' : 'info'
}
export function notify(text: string | null, tone?: ToastTone) {
  if (text == null || text === '') return
  const item: ToastItem = { id: _toastId++, text, tone: tone ?? inferTone(text) }
  _toasts = [..._toasts, item].slice(-4) // keep the last few; a flood self-trims
  _emitToasts()
  const ms = Math.min(12000, 3500 + text.length * 35) // linger longer for longer messages (e.g. a reconcile digest)
  setTimeout(() => dismissToast(item.id), ms)
}
function useToast() { return notify }

function ToastHost() {
  const [items, setItems] = useState<ToastItem[]>(_toasts)
  useEffect(() => { _toastSubs.add(setItems); setItems(_toasts); return () => { _toastSubs.delete(setItems) } }, [])
  if (items.length === 0) return null
  return (
    <div className="toast-host" role="region" aria-label="Notifications" aria-live="polite">
      {items.map(t => (
        <div key={t.id} className={`toast ${t.tone}`} role="status" onClick={() => dismissToast(t.id)} title="Dismiss">
          <span className="toast-dot" /><span className="toast-text">{t.text}</span>
        </div>
      ))}
    </div>
  )
}

type Tab = 'dashboard' | 'airline' | 'jobs' | 'clients' | 'flight' | 'hangar' | 'ops' | 'bases' | 'trade' | 'finances' | 'campaigns' | 'awards' | 'logbook' | 'settings'

export function App() {
  const [state, setState] = useState<State | null | undefined>(undefined) // undefined = still loading
  const [tab, setTab] = useState<Tab>('dashboard')
  const [error, setError] = useState<string | null>(null)
  const [airline, setAirline] = useState<AirlineData | null>(null)

  // Per-tab first-visit tutorials: show the guide the first time a tab is opened; remember it per device.
  const [seenTabs, setSeenTabs] = useState<Set<string>>(loadSeenTabs)
  const [guide, setGuide] = useState<Tab | null>(null)
  useEffect(() => { if (state && !seenTabs.has(tab) && TAB_GUIDES[tab]) setGuide(tab) }, [tab, state]) // eslint-disable-line react-hooks/exhaustive-deps
  const closeGuide = useCallback(() => {
    if (guide) setSeenTabs(prev => { const next = new Set(prev); next.add(guide); saveSeenTabs(next); return next })
    setGuide(null)
  }, [guide])

  // Grow the shared aircraft catalog with the types this install knows (facts only, best-effort).
  useEffect(() => { void api.cloud.reportAircraft().catch(() => undefined) }, [])

  const reload = useCallback(async () => {
    try {
      setState(await api.state())
    } catch (e) {
      setError(String(e))
    }
  }, [])
  const loadAirline = useCallback(() => { api.airline().then(setAirline).catch(() => {}) }, [])

  useEffect(() => { void reload() }, [reload])
  useEffect(() => { if (state) loadAirline() }, [state, loadAirline])

  // Global notifications (Phase 13): autonomous crew work that banks in the background pushes a toast on ANY tab,
  // and refreshes state so the newly-banked cash shows. Reuses the telemetry socket the server broadcasts on.
  useEffect(() => {
    let ws: WebSocket | null = null, closed = false, retry: ReturnType<typeof setTimeout>
    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      ws = new WebSocket(`${proto}://${location.host}/ws/telemetry`)
      ws.onmessage = e => {
        try {
          const m = JSON.parse(e.data) as { type?: string; text?: string }
          if (m.type === 'notify' && m.text) { notify(m.text, 'ok'); void reload() }
        } catch { /* ignore malformed frames */ }
      }
      ws.onclose = () => { if (!closed) retry = setTimeout(connect, 2000) }
      ws.onerror = () => ws?.close()
    }
    connect()
    return () => { closed = true; clearTimeout(retry); ws?.close() }
  }, [reload])

  if (state === undefined) return error
    ? <StartupError error={error} onRetry={() => { setError(null); void reload() }} />
    : <Splash />
  if (state === null) return <Onboarding onStarted={reload} />

  return (
    <div className="shell">
      <ToastHost />
      <TitleBar />
      <div className="app">
      <NavRail tab={tab} setTab={setTab} airline={airline} />
      <div className="work">
        <ContextHeader state={state} tab={tab} onHelp={TAB_GUIDES[tab] ? () => setGuide(tab) : undefined} />
        <main className="main">
        {error && <div className="banner error" onClick={() => setError(null)}>{error} — tap to dismiss</div>}
        {tab === 'dashboard' && <Dashboard state={state} airline={airline} go={setTab} />}
        {tab === 'airline' && <Airline onSaved={() => { void reload(); loadAirline() }} />}
        {tab === 'jobs' && <Jobs state={state} onChanged={reload} />}
        {tab === 'clients' && <Clients />}
        {tab === 'flight' && <Flight state={state} onSettled={reload} />}
        {tab === 'hangar' && <Hangar state={state} onChanged={reload} />}
        {tab === 'ops' && <Ops onChanged={reload} />}
        {tab === 'bases' && <Bases state={state} onChanged={reload} />}
        {tab === 'trade' && <Trade state={state} onChanged={reload} />}
        {tab === 'finances' && <Finances state={state} onChanged={reload} />}
        {tab === 'campaigns' && <Campaigns onChanged={reload} />}
        {tab === 'awards' && <Awards />}
        {tab === 'logbook' && <Logbook state={state} />}
        {tab === 'settings' && <Settings />}
        </main>
      </div>
      </div>
      {guide && <TabGuide tab={guide} onClose={closeGuide} />}
    </div>
  )
}

// ─── Desktop window chrome: a frameless title bar (only inside the WebView2 shell) ───

// True only in the packaged desktop app (the WebView2 host), never in a plain browser.
const inWebView = typeof window !== 'undefined' && !!(window as { chrome?: { webview?: unknown } }).chrome?.webview
function winCmd(cmd: string) {
  try { (window as unknown as { chrome: { webview: { postMessage: (m: string) => void } } }).chrome.webview.postMessage(cmd) } catch { /* not in the desktop shell */ }
}

// The CALL·SIGN top bar: brand + minimize / maximize / close, and a drag region — matching the launcher.
// Rendered only in the desktop app; in a browser the native/browser chrome is kept, so dev is unaffected.
function TitleBar() {
  if (!inWebView) return null
  return (
    <div className="wtitlebar"
      onPointerDown={e => { if ((e.target as HTMLElement).closest('.wbtn')) return; if (e.button === 0) winCmd('win:drag') }}
      onDoubleClick={() => winCmd('win:maximize')}>
      <span className="wbrand">CALL<span className="dot">·</span>SIGN</span>
      <span className="wsp" />
      <button className="wbtn" title="Minimize" aria-label="Minimize" onClick={() => winCmd('win:minimize')}>&#x2013;</button>
      <button className="wbtn" title="Maximize" aria-label="Maximize" onClick={() => winCmd('win:maximize')}>&#x25A1;</button>
      <button className="wbtn close" title="Close" aria-label="Close" onClick={() => winCmd('win:close')}>&#x2715;</button>
    </div>
  )
}

// ─── Shell: nav rail + context header ────────────────────────────────────────

const TABS: { id: Tab; label: string; sub: string }[] = [
  { id: 'dashboard', label: 'Dashboard', sub: 'Your operation at a glance' },
  { id: 'airline', label: 'Airline', sub: 'Identity & standing' },
  { id: 'jobs', label: 'Jobs', sub: 'Find and accept work' },
  { id: 'clients', label: 'Clients', sub: 'Who you fly for' },
  { id: 'flight', label: 'Flight', sub: 'Fly your objectives' },
  { id: 'hangar', label: 'Hangar', sub: 'Your fleet & the market' },
  { id: 'ops', label: 'Staff', sub: 'Crew, standing orders & routes' },
  { id: 'bases', label: 'Bases', sub: 'Your network' },
  { id: 'trade', label: 'Trade', sub: 'The commodity market' },
  { id: 'finances', label: 'Finances', sub: 'Balance sheet, P&L & loans' },
  { id: 'campaigns', label: 'Campaigns', sub: 'Fly a story' },
  { id: 'awards', label: 'Awards', sub: 'Achievements earned' },
  { id: 'logbook', label: 'Logbook', sub: 'Flights & the ledger' },
  { id: 'settings', label: 'Settings', sub: 'Preferences & your save' },
]

// ── Per-tab first-visit tutorials (Phase 12): the first time you open a tab, a short card explains what
//    it's for and what you can do there. "Seen" is remembered per device; a ? in the header reopens it. ──
const SEEN_TABS_KEY = 'callsign.seenTabs.v1'
function loadSeenTabs(): Set<string> {
  try { return new Set(JSON.parse(localStorage.getItem(SEEN_TABS_KEY) ?? '[]') as string[]) } catch { return new Set() }
}
function saveSeenTabs(s: Set<string>) {
  try { localStorage.setItem(SEEN_TABS_KEY, JSON.stringify([...s])) } catch { /* private mode — just teach again next time */ }
}

const TAB_GUIDES: Partial<Record<Tab, { title: string; lead: string; points: string[] }>> = {
  dashboard: {
    title: 'Your command deck', lead: 'Everything about your operation, at a glance.',
    points: [
      'Net worth, cash and profit run across the top — tap any stat to jump to its detail.',
      'The map shows your bases, every aircraft, and the legs in the air right now.',
      'Alerts flag anything that needs you — a service due, a lapsing certificate, a loan.',
    ],
  },
  airline: {
    title: 'Your identity & standing', lead: 'This is your airline — its name, its look, and how far it has climbed.',
    points: [
      'Name the airline and choose a tail code, accent colour and emblem.',
      'Track your operating reputation and your rung on the career ladder.',
      'Reputation is earned by flying well — and it lifts what your hubs pay.',
    ],
  },
  jobs: {
    title: 'The job board', lead: 'Work waiting at the field you’re parked at right now.',
    points: [
      'Cargo, passengers and charters — each shows its pay, distance and what it needs.',
      'Pay is locked in the moment you accept, so weather and the market can’t claw it back.',
      'A ⌂ +$ note means your reputation is lifting the pay at this hub.',
      'Accept a job here, then fly it on the Flight tab.',
    ],
  },
  clients: {
    title: 'Who you fly for', lead: 'The carriers and companies whose work you take on.',
    points: [
      'Completing a client’s jobs builds loyalty over time.',
      'Loyal clients pay a premium — and drift back down if you neglect them.',
    ],
  },
  flight: {
    title: 'Fly your objectives', lead: 'Where an accepted job becomes a real flight in the simulator.',
    points: [
      'Pick an accepted job, load up, and fly it in MSFS.',
      'Callsign watches your takeoff, cruise and landing live — nothing to press.',
      'Land well and you’re scored; the score drives your pay and your reputation.',
    ],
  },
  hangar: {
    title: 'Your fleet & the market', lead: 'Every aircraft you own, and where to buy more.',
    points: [
      'Service and inspect your planes to keep them airworthy.',
      'Buy new or used aircraft as your bankroll grows.',
      'Each shows its condition, value and the class rating it needs.',
    ],
  },
  ops: {
    title: 'Crew, standing orders & routes', lead: 'Grow beyond flying every leg yourself.',
    points: [
      'Hire crew to fly legs autonomously while you do other things.',
      'Set standing orders and run scheduled routes for steady income.',
      'Your crew’s skill shapes how those legs pay and how your reputation settles.',
    ],
  },
  bases: {
    title: 'Your network', lead: 'The airports you operate from.',
    points: [
      'Open a base to land there fee-free and park aircraft.',
      'Upgrade a base into a hub — it amplifies the pay your reputation earns at that field.',
      'Bases carry a daily cost, so open them where you actually fly.',
    ],
  },
  trade: {
    title: 'The commodity market', lead: 'Buy low at one field, sell high at another.',
    points: [
      'Prices differ by airport and drift with the economy and the weather.',
      'Carry goods in your aircraft to move them — your hold is your capacity.',
    ],
  },
  finances: {
    title: 'The books', lead: 'The full financial picture of your airline.',
    points: [
      'Balance sheet, profit & loss, and any loans you carry.',
      'Every cent runs through an append-only ledger — nothing is hidden.',
    ],
  },
  campaigns: {
    title: 'Fly a story', lead: 'Curated multi-leg campaigns with a payoff at the end.',
    points: [
      'Follow a themed set of flights across a region.',
      'Finish one for a reward and a mark on your record.',
    ],
  },
  awards: {
    title: 'Achievements earned', lead: 'The milestones you unlock as you fly.',
    points: ['A record of what you’ve accomplished, from first flights to long-haul feats.'],
  },
  logbook: {
    title: 'Flights & the ledger', lead: 'The complete history of your career.',
    points: [
      'Every flight you’ve flown, sortable by score, distance or date.',
      'The full money trail sits alongside it — earnings, costs, everything.',
    ],
  },
  settings: {
    title: 'Preferences & your save', lead: 'Make Callsign yours, and keep your career safe.',
    points: ['Theme and motion settings.', 'Back your career up to the cloud, or manage local save files.'],
  },
}

function TabGuide({ tab, onClose }: { tab: Tab; onClose: () => void }) {
  const g = TAB_GUIDES[tab]
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  if (!g) return null
  return (
    <div className="guide-backdrop" onClick={onClose}>
      <div className="guide-card" role="dialog" aria-modal="true" aria-label={g.title} onClick={e => e.stopPropagation()}>
        <div className="guide-kicker">Quick guide</div>
        <h2>{g.title}</h2>
        <p className="guide-lead">{g.lead}</p>
        <ul className="guide-points">{g.points.map((p, i) => <li key={i}>{p}</li>)}</ul>
        <div className="guide-foot">
          <span className="guide-hint">Reopen any time with the <b>?</b> up top.</span>
          <button className="primary" onClick={onClose}>Got it →</button>
        </div>
      </div>
    </div>
  )
}

// The rail's five clear areas (redesign R1) — fifteen flat tabs, chunked so the eye reads a short list of
// purposes, not a menu of everything. Each group's label sits above its icons; "You" pins to the bottom.
const NAV_GROUPS: { label: string; tabs: Tab[] }[] = [
  { label: 'Fly', tabs: ['dashboard', 'jobs', 'flight'] },
  { label: 'Fleet', tabs: ['hangar', 'bases'] },
  { label: 'Company', tabs: ['ops', 'airline', 'finances', 'trade', 'clients'] },
  { label: 'Career', tabs: ['campaigns', 'awards', 'logbook'] },
]

function NavRail({ tab, setTab, airline }: { tab: Tab; setTab: (t: Tab) => void; airline: AirlineData | null }) {
  const item = (t: Tab, label: string) => (
    <button key={t} className={`ric ${tab === t ? 'on' : ''}`} onClick={() => setTab(t)} aria-label={label}>
      {navIcon(t)}<span className="tip">{label}</span>
    </button>
  )
  const labelOf = (t: Tab) => TABS.find(x => x.id === t)?.label ?? t
  return (
    <aside className="rail">
      <button className="rail-emblem" title="Airline identity" onClick={() => setTab('airline')} aria-label="Airline">
        {airline
          ? <Emblem emblem={airline.identity.emblemKey} color={airline.identity.accentColorHex} size={34} />
          : <span className="mark" style={{ fontSize: 24 }}>◄</span>}
      </button>
      {NAV_GROUPS.map(g => (
        <div className="rail-group" key={g.label}>
          <div className="rail-grouplabel">{g.label}</div>
          {g.tabs.map(t => item(t, labelOf(t)))}
        </div>
      ))}
      <div className="rail-group rail-group-end">
        <div className="rail-grouplabel">You</div>
        {item('settings', 'Settings')}
      </div>
    </aside>
  )
}

// Weather at the current field is rough enough to matter to a departure (Phase 8) — flag it.
const roughWx = (c: string) => c === 'Storm' || c === 'Fog' || c === 'Snow'

function ContextHeader({ state, tab, onHelp }: { state: State; tab: Tab; onHelp?: () => void }) {
  const meta = TABS.find(t => t.id === tab)
  const [wx, setWx] = useState<Weather | null>(null)
  const [world, setWorld] = useState<WorldState | null>(null)
  useEffect(() => {
    let live = true
    const load = () => {
      api.weather().then(w => { if (live) setWx(w) }).catch(() => { if (live) setWx(null) })
      api.world().then(w => { if (live) setWorld(w) }).catch(() => { if (live) setWorld(null) })
    }
    load()
    const t = setInterval(load, 5 * 60_000) // weather + calendar hold for a window; refresh as time advances
    return () => { live = false; clearInterval(t) }
  }, [state.currentIcao])
  return (
    <header className="ctxbar">
      <div className="ctx-title">
        <div className="ctx-titlerow">
          <h1>{meta?.label ?? 'Callsign'}</h1>
          {onHelp && <button className="ctx-help" onClick={onHelp} title="What is this tab?" aria-label="What is this tab?">?</button>}
        </div>
        <div className="sub">{meta?.sub ?? ''}</div>
      </div>
      <div className="ctx">
        <span className="chip"><span className="dot" /> <b className="loc">{state.currentIcao}</b></span>
        {world && <span className="chip" title={`${world.dayOfWeek}, ${world.dateIso} · ${world.season}${world.season === 'Winter' || world.season === 'Autumn' ? ' — busy season, jobs pay a little more' : world.season === 'Summer' ? ' — quiet season, jobs pay a little less' : ''}`}>{world.season} · <span className="muted">day</span> <span className="num">{world.careerDays.toLocaleString()}</span></span>}
        {world && <span className={`chip econ ${world.economyRewardPct > 0 ? 'up' : world.economyRewardPct < 0 ? 'down' : ''}`} title={`Macro economy: ${world.economyLabel} — fresh job pay is ${world.economyRewardPct >= 0 ? '+' : ''}${world.economyRewardPct}% versus par right now`}>{world.economyLabel}{world.economyRewardPct !== 0 && <> · <span className="num">{world.economyRewardPct >= 0 ? '+' : ''}{world.economyRewardPct}%</span></>}</span>}
        {wx && <span className={`chip wx${roughWx(wx.condition) ? ' rough' : ''}`} title={wx.live
          ? `${wx.name}: real METAR${wx.stationIcao ? ` (${wx.stationIcao})` : ''} — ${wx.summary}${wx.observedIso ? ` · as of ${wx.observedIso.slice(11, 16)}Z` : ''}`
          : `${wx.name}: ${wx.summary} · modeled · gust ${wx.gustKts} kt · ceiling ${wx.ceilingFt.toLocaleString()} ft`}>
          {wx.condition} · <span className="num">{wx.windKts}</span> kt · <span className="num">{wx.tempC}</span>°C{wx.live && <span className="wx-live">LIVE</span>}
        </span>}
        <span className="chip pilot-chip"><Avatar src={state.avatarKey} name={state.name} size={18} /> {state.name} · <span className="muted">{state.rank}</span> · {state.xp.toLocaleString()} XP</span>
        <span className="chip cash"><b className="num">{money(state.cashCents)}</b></span>
      </div>
    </header>
  )
}

function navIcon(id: Tab) {
  switch (id) {
    case 'dashboard': return <svg viewBox="0 0 24 24"><rect x="3" y="3" width="8" height="8" rx="1.5" /><rect x="13" y="3" width="8" height="5" rx="1.5" /><rect x="13" y="11" width="8" height="10" rx="1.5" /><rect x="3" y="14" width="8" height="7" rx="1.5" /></svg>
    case 'airline': return <svg viewBox="0 0 24 24"><path d="M3 15l18-7-7 13-3-5-8-1z" /></svg>
    case 'jobs': return <svg viewBox="0 0 24 24"><rect x="3" y="7" width="18" height="13" rx="2" /><path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></svg>
    case 'clients': return <svg viewBox="0 0 24 24"><circle cx="9" cy="8.5" r="3.2" /><path d="M3.5 20c0-3.2 2.5-5.4 5.5-5.4s5.5 2.2 5.5 5.4" /><path d="M18.7 9c-1.9-1.4-3.1-2.5-3.1-3.8 0-1 .8-1.7 1.7-1.7 .6 0 1.1 .3 1.4 .8 .3-.5 .8-.8 1.4-.8 .9 0 1.7 .7 1.7 1.7 0 1.3-1.2 2.4-3.1 3.8z" /></svg>
    case 'flight': return <svg viewBox="0 0 24 24"><path d="M21 15v-2l-8-5V3.5a1.5 1.5 0 0 0-3 0V8l-8 5v2l8-2.5V18l-2 1.5V21l3.5-1 3.5 1v-1.5L12 18v-5.5l9 2.5z" /></svg>
    case 'hangar': return <svg viewBox="0 0 24 24"><path d="M3 10l9-5 9 5" /><path d="M5 10v10h14V10" /><path d="M9 20v-6h6v6" /></svg>
    case 'ops': return <svg viewBox="0 0 24 24"><circle cx="9" cy="8" r="3" /><path d="M4 20c0-3 2.5-5 5-5s5 2 5 5" /><path d="M16 6a3 3 0 0 1 0 6M20 20c0-2.4-1.4-4.3-3.5-4.8" /></svg>
    case 'bases': return <svg viewBox="0 0 24 24"><path d="M12 2l8 5v13H4V7l8-5z" /><path d="M9 20v-6h6v6" /></svg>
    case 'trade': return <svg viewBox="0 0 24 24"><path d="M4 5h2l2 11h9l2-8H7" /><circle cx="9" cy="20" r="1.4" /><circle cx="17" cy="20" r="1.4" /></svg>
    case 'finances': return <svg viewBox="0 0 24 24"><ellipse cx="12" cy="6" rx="7" ry="3" /><path d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6" /><path d="M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6" /></svg>
    case 'campaigns': return <svg viewBox="0 0 24 24"><path d="M5 21V4c3-2 6 2 9 0v9c-3 2-6-2-9 0" /></svg>
    case 'awards': return <svg viewBox="0 0 24 24"><circle cx="12" cy="9" r="5" /><path d="M9 13l-2 8 5-3 5 3-2-8" /></svg>
    case 'logbook': return <svg viewBox="0 0 24 24"><path d="M5 4h11a2 2 0 0 1 2 2v14H7a2 2 0 0 1-2-2V4z" /><path d="M9 8h6M9 12h6" /></svg>
    case 'settings': return <svg viewBox="0 0 24 24"><path d="M4 7h10M18 7h2M4 17h2M10 17h10" /><circle cx="16" cy="7" r="2.3" /><circle cx="8" cy="17" r="2.3" /></svg>
    default: return null
  }
}

function Splash() {
  return <div className="splash"><div className="mark big">◄</div><div>Loading Callsign…</div></div>
}

// Shown when the very first load fails for a reason other than "no career yet" (a 500, a locked DB, the
// Host not up) — otherwise a non-404 error would strand the user on the loading Splash forever.
function StartupError({ error, onRetry }: { error: string; onRetry: () => void }) {
  return (
    <div className="onboard">
      <div className="onboard-card">
        <div className="onboard-body">
          <div className="brand"><span className="mark">◄</span> CALLSIGN</div>
          <h1>Couldn't load your career</h1>
          <p className="lede">Callsign reached the app but something went wrong loading your save. It's still on disk — this is usually temporary.</p>
          <div className="banner error">{error}</div>
          <div className="onboard-foot"><span /><button className="primary" onClick={onRetry}>Try again</button></div>
        </div>
      </div>
    </div>
  )
}

// ─── Onboarding (first run) ───────────────────────────────────────────────────

/** A minimal live link to the sim for the onboarding "connect" step — the same reconnecting
 *  WebSocket as useTelemetry, without the flight/settlement plumbing. */
function useSimLink() {
  const [wsOpen, setWsOpen] = useState(false)
  const [link, setLink] = useState('Disconnected')
  useEffect(() => {
    let ws: WebSocket | null = null
    let closed = false
    let retry: ReturnType<typeof setTimeout>
    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      ws = new WebSocket(`${proto}://${location.host}/ws/telemetry`)
      ws.onopen = () => setWsOpen(true)
      ws.onmessage = e => {
        const m = JSON.parse(e.data) as WsEvent
        if (m.type === 'telemetry' || m.type === 'state') setLink(m.connection)
      }
      ws.onclose = () => { setWsOpen(false); if (!closed) retry = setTimeout(connect, 1500) }
      ws.onerror = () => ws?.close()
    }
    connect()
    return () => { closed = true; clearTimeout(retry); ws?.close() }
  }, [])
  return { wsOpen, link }
}

// Every new career starts with the same $10,000 bankroll (Phase 12 onboarding) — a lean, level start.
const START_CASH = 10000

// The pilot's avatar (stored in Pilot.AvatarKey) is either an uploaded image — a compact data: URL — or,
// when none is set, a clean initials monogram derived from the callsign, so it always looks intentional.
function initialsOf(name?: string | null) {
  const parts = (name || '').trim().split(/\s+/).filter(Boolean)
  if (!parts.length) return '✈'
  return (parts.length === 1 ? parts[0].slice(0, 2) : parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}
function hueOf(name?: string | null) {
  let h = 0
  for (const c of (name || 'callsign')) h = (h * 31 + c.charCodeAt(0)) >>> 0
  return h % 360
}
function Avatar({ src, name, size = 40 }: { src?: string | null; name?: string | null; size?: number }) {
  const dim: CSSProperties = { width: size, height: size }
  if (src && src.startsWith('data:'))
    return <img className="avatar-img" src={src} alt="" style={dim} />
  return (
    <span className="avatar-mono" style={{ ...dim, background: `hsl(${hueOf(name)} 42% 34%)`, fontSize: Math.round(size * 0.4) }}>
      {initialsOf(name)}
    </span>
  )
}

// Read an image file, cover-crop it to a small square, and return a compact JPEG data URL for the avatar.
function fileToAvatar(file: File): Promise<string | null> {
  return new Promise(resolve => {
    const reader = new FileReader()
    reader.onload = () => {
      const img = new Image()
      img.onload = () => {
        const S = 160
        const canvas = document.createElement('canvas')
        canvas.width = canvas.height = S
        const ctx = canvas.getContext('2d')
        if (!ctx) return resolve(null)
        const scale = Math.max(S / img.width, S / img.height)
        const w = img.width * scale, h = img.height * scale
        ctx.drawImage(img, (S - w) / 2, (S - h) / 2, w, h)
        resolve(canvas.toDataURL('image/jpeg', 0.82))
      }
      img.onerror = () => resolve(null)
      img.src = reader.result as string
    }
    reader.onerror = () => resolve(null)
    reader.readAsDataURL(file)
  })
}

// The three MSFS 2024 editions the player can flag — a profile badge only, never a content gate.
const EDITIONS = [
  { key: 'Standard', blurb: 'The core MSFS 2024 fleet and world.' },
  { key: 'Premium', blurb: 'Standard plus a set of extra high-fidelity aircraft.' },
  { key: 'Deluxe', blurb: 'The fullest fleet — every premium aircraft and airport.' },
]

// The starter trio — codes match the curated catalog types seeded at career start.
const STARTERS = [
  { code: 'C152', name: 'Cessna 152', tag: 'Trainer', cruise: 107, seats: 2, blurb: 'The classic trainer — docile, cheap, forgiving. The safe first step.' },
  { code: 'DR40', name: 'Robin DR400', tag: 'Tourer', cruise: 130, seats: 4, blurb: 'A sprightly tourer — roomier and quicker, a little more to handle.' },
  { code: 'VL3', name: 'JMB VL3', tag: 'Speedster', cruise: 150, seats: 2, blurb: 'A slippery modern speedster — fastest here, and the least forgiving.' },
]

function Onboarding({ onStarted }: { onStarted: () => void }) {
  const [step, setStep] = useState(0) // 0 account · 1 edition · 2 aircraft · 3 pilot · 4 ready

  // Step 0 — account (Callsign Cloud). Account-first, but "Continue offline" never locks anyone out.
  const [cloud, setCloud] = useState<CloudStatus | null>(null)
  const [mode, setMode] = useState<'login' | 'register'>('register')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [dispName, setDispName] = useState('')
  const [authBusy, setAuthBusy] = useState(false)
  const [authErr, setAuthErr] = useState<string | null>(null)

  // Career choices
  const [edition, setEdition] = useState('Standard')
  const [starter, setStarter] = useState('C152')
  const [callsign, setCallsign] = useState('')
  const [avatar, setAvatar] = useState('') // '' = initials monogram; else a data: URL of an uploaded photo
  const [home, setHome] = useState('EHAM')

  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  const { wsOpen, link } = useSimLink()
  const connected = link === 'Connected'

  useEffect(() => {
    api.cloud.status()
      .then(s => { setCloud(s); if (s.displayName) setCallsign(prev => prev || s.displayName!) })
      .catch(() => setCloud({ signedIn: false, baseUrl: '' }))
  }, [])

  const signedIn = cloud?.signedIn === true
  const canAuth = email.trim().length > 0 && password.length > 0 && (mode === 'login' || dispName.trim().length > 0)
  const canPilot = callsign.trim().length > 0 && home.trim().length >= 3
  const chosen = STARTERS.find(s => s.code === starter) ?? STARTERS[0]

  const doAuth = async () => {
    if (!canAuth) return
    setAuthBusy(true); setAuthErr(null)
    try {
      const r = mode === 'login'
        ? await api.cloud.login(email.trim(), password)
        : await api.cloud.register(email.trim(), dispName.trim(), password)
      if (!r.ok) { setAuthErr(r.error ?? 'That didn’t work — check your details and try again.'); return }
      const s = await api.cloud.status()
      setCloud(s)
      if (s.displayName) setCallsign(prev => prev || s.displayName!)
      setStep(1)
    } catch (e) {
      setAuthErr(String(e))
    } finally { setAuthBusy(false) }
  }

  const signOut = async () => {
    try { await api.cloud.logout() } catch { /* ignore */ }
    setCloud(await api.cloud.status().catch(() => ({ signedIn: false, baseUrl: '' })))
  }

  const commit = async () => {
    setBusy(true); setErr(null)
    try {
      await api.newCareer(callsign.trim() || 'New Pilot', home.trim().toUpperCase() || 'EHAM', START_CASH,
        { starterTypeCode: starter, edition, avatarKey: avatar })
      onStarted()
    } catch (e) {
      setErr(String(e)); setBusy(false)
    }
  }

  return (
    <div className="onboard">
      <div className="onboard-card">
        <div className="onboard-top">
          <div className="brand"><span className="mark">◄</span> CALLSIGN</div>
          <div className="step-dots">
            {[0, 1, 2, 3, 4].map(i => <span key={i} className={`dot ${i === step ? 'on' : ''} ${i < step ? 'done' : ''}`} />)}
          </div>
        </div>

        {step === 0 && (
          <div className="onboard-body" key="s0">
            <h1>Welcome to Callsign</h1>
            <p className="lede">A living career for Microsoft Flight Simulator 2024 — fly for hire, build an airline, climb the ranks. An account backs your career up to the cloud and carries it to any PC.</p>
            {signedIn ? (
              <>
                <div className="simlink up">
                  <span className="simdot up" />
                  <div>
                    <div className="simlink-state">Signed in as {cloud?.displayName}</div>
                    <div className="simlink-sub">{cloud?.email} · your career will back up to the cloud.</div>
                  </div>
                </div>
                <div className="onboard-foot">
                  <button className="linky" onClick={signOut}>Not you? Sign out</button>
                  <button className="primary" onClick={() => setStep(1)}>Continue →</button>
                </div>
              </>
            ) : (
              <>
                <div className="seg">
                  <button className={`seg-btn ${mode === 'register' ? 'on' : ''}`} onClick={() => setMode('register')}>Create account</button>
                  <button className={`seg-btn ${mode === 'login' ? 'on' : ''}`} onClick={() => setMode('login')}>Sign in</button>
                </div>
                <div className="form">
                  <label className="fld"><span>Email</span>
                    <input type="email" autoComplete="username" value={email} placeholder="you@example.com" onChange={e => setEmail(e.target.value)} />
                  </label>
                  {mode === 'register' && (
                    <label className="fld"><span>Callsign / display name</span>
                      <input value={dispName} maxLength={40} placeholder="e.g. Maverick" onChange={e => setDispName(e.target.value)} />
                    </label>
                  )}
                  <label className="fld"><span>Password</span>
                    <input type="password" autoComplete={mode === 'login' ? 'current-password' : 'new-password'} value={password}
                      placeholder={mode === 'register' ? 'At least 8 characters' : ''} onChange={e => setPassword(e.target.value)}
                      onKeyDown={e => { if (e.key === 'Enter') void doAuth() }} />
                  </label>
                </div>
                {authErr && <div className="banner error">{authErr}</div>}
                <div className="onboard-foot">
                  <button className="ghost" onClick={() => setStep(1)}>Continue offline →</button>
                  <button className="primary" disabled={authBusy || !canAuth} onClick={doAuth}>
                    {authBusy ? 'One moment…' : mode === 'register' ? 'Create account →' : 'Sign in →'}
                  </button>
                </div>
                <p className="ob-hint">No internet, or want to jump straight in? <b>Continue offline</b> — the game never needs an account, and you can link one later in Settings.</p>
              </>
            )}
          </div>
        )}

        {step === 1 && (
          <div className="onboard-body" key="s1">
            <h1>Your simulator edition</h1>
            <p className="lede">Which edition of MSFS 2024 do you fly? This is your profile badge — it never limits what you can do in Callsign.</p>
            <div className="presets">
              {EDITIONS.map(e => (
                <button key={e.key} type="button" className={`preset ${edition === e.key ? 'on' : ''}`} onClick={() => setEdition(e.key)}>
                  <div className="preset-amt">{e.key}</div>
                  <div className="preset-blurb">{e.blurb}</div>
                </button>
              ))}
            </div>
            <div className="onboard-foot">
              <button className="ghost" onClick={() => setStep(0)}>← Back</button>
              <button className="primary" onClick={() => setStep(2)}>Continue →</button>
            </div>
          </div>
        )}

        {step === 2 && (
          <div className="onboard-body" key="s2">
            <h1>Pick your first aircraft</h1>
            <p className="lede">Your career opens with one plane, parked and paid for. Buy more the moment you’ve earned it.</p>
            <div className="presets">
              {STARTERS.map(s => (
                <button key={s.code} type="button" className={`preset pick ${starter === s.code ? 'on' : ''}`} onClick={() => setStarter(s.code)}>
                  <div className="pick-tag">{s.tag}</div>
                  <div className="preset-amt">{s.name}</div>
                  <div className="preset-blurb">{s.blurb}</div>
                  <div className="pick-specs num">{s.seats} seats · {s.cruise} kt cruise</div>
                </button>
              ))}
            </div>
            <div className="onboard-foot">
              <button className="ghost" onClick={() => setStep(1)}>← Back</button>
              <button className="primary" onClick={() => setStep(3)}>Continue →</button>
            </div>
          </div>
        )}

        {step === 3 && (
          <div className="onboard-body" key="s3">
            <h1>Create your pilot</h1>
            <p className="lede">This is you in the world. You can rebrand your airline any time later.</p>
            <label className="ob-field">Callsign
              <input autoFocus value={callsign} placeholder="e.g. Maverick" onChange={e => setCallsign(e.target.value)} />
            </label>
            <div className="ob-field">Avatar
              <div className="avatar-edit">
                <Avatar src={avatar} name={callsign} size={64} />
                <div className="avatar-actions">
                  <label className="avatar-upload">
                    {avatar ? 'Change photo' : 'Upload a photo'}
                    <input type="file" accept="image/*" onChange={async e => { const f = e.target.files?.[0]; e.currentTarget.value = ''; if (f) setAvatar(await fileToAvatar(f) ?? '') }} />
                  </label>
                  {avatar
                    ? <button type="button" className="linky" onClick={() => setAvatar('')}>Remove</button>
                    : <span className="ob-hint">Optional — otherwise your callsign initials are used.</span>}
                </div>
              </div>
            </div>
            <label className="ob-field">Home base — ICAO code
              <input value={home} maxLength={4} placeholder="EHAM" onChange={e => setHome(e.target.value.toUpperCase())} />
              <span className="ob-hint">Where your aircraft is parked and lands fee-free. Four letters — e.g. KJFK, EGLL, EHAM.</span>
            </label>
            <div className="onboard-foot">
              <button className="ghost" onClick={() => setStep(2)}>← Back</button>
              <button className="primary" disabled={!canPilot} onClick={() => setStep(4)}>Continue →</button>
            </div>
          </div>
        )}

        {step === 4 && (
          <div className="onboard-body" key="s4">
            <h1>Cleared for departure</h1>
            <p className="lede">Here’s your setup. Start flying and your first jobs will be waiting.</p>
            <div className="summary">
              <div className="srow"><span className="muted">Pilot</span><b className="pilot-cell"><Avatar src={avatar} name={callsign} size={22} /> {callsign.trim() || 'New Pilot'}</b></div>
              <div className="srow"><span className="muted">First aircraft</span><b>{chosen.name}</b></div>
              <div className="srow"><span className="muted">Home base</span><b className="loc">{home.trim().toUpperCase() || 'EHAM'}</b></div>
              <div className="srow"><span className="muted">Edition</span><b>{edition}</b></div>
              <div className="srow"><span className="muted">Starting bankroll</span><b className="num">{money(START_CASH * 100)}</b></div>
              <div className="srow"><span className="muted">Account</span><b className={signedIn ? 'pos' : 'muted'}>{signedIn ? cloud?.displayName : 'Offline'}</b></div>
              <div className="srow"><span className="muted">Simulator</span><b className={connected ? 'pos' : 'muted'}>{connected ? 'Connected' : wsOpen ? 'Waiting…' : 'Connect later'}</b></div>
            </div>
            {err && <div className="banner error">{err}</div>}
            <div className="onboard-foot">
              <button className="ghost" disabled={busy} onClick={() => setStep(3)}>← Back</button>
              <button className="primary" disabled={busy} onClick={commit}>{busy ? 'Setting up your world…' : 'Start flying →'}</button>
            </div>
            {busy && <p className="ob-hint">First run imports a public-domain airport database — this can take a minute.</p>}
          </div>
        )}
      </div>
    </div>
  )
}

// ─── Dashboard ───────────────────────────────────────────────────────────────
// The operations command deck: an image-forward hero with a live sim-link, a rich stat row, an
// Operations-status alerts panel, a satellite network map (bases + every airframe + active legs + the
// live aircraft), a fleet carousel with drill-down, trend charts, a finances snapshot, standing
// breakdown, active-campaign progress, recent flights, and a live activity feed. Built almost entirely
// from endpoints the rest of the app already serves — the Dashboard is the one screen that sees it all.

// Availability → marker colour (network map) and → status-dot class (fleet chips).
const AVAIL_TONE: Record<string, string> = {
  Available: '#3ecf8e', InFlight: '#6d84ff', Reserved: '#d99a1c', Grounded: '#f26a5c',
}
const AVAIL_KEY: Record<string, string> = {
  Available: 'ok', InFlight: 'fly', Reserved: 'rsv', Grounded: 'gnd',
}

interface DashAlert { level: 'bad' | 'warn' | 'info'; text: string; tab?: Tab; cta?: string }

function Dashboard({ state, airline, go }: { state: State; airline: AirlineData | null; go: (t: Tab) => void }) {
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [ranks, setRanks] = useState<RankTier[]>([])
  const [rep, setRep] = useState<Reputation | null>(null)
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [fin, setFin] = useState<FinancesData | null>(null)
  const [bases, setBases] = useState<BaseView[]>([])
  const [flights, setFlights] = useState<FlightLog[]>([])
  const [ledger, setLedger] = useState<LedgerEntry[]>([])
  const [campaigns, setCampaigns] = useState<Campaign[]>([])
  const [challenges, setChallenges] = useState<Challenge[]>([])
  const [highlights, setHighlights] = useState<CareerHighlights | null>(null)
  const [staff, setStaff] = useState<Staff[]>([])
  const [routes, setRoutes] = useState<RouteData | null>(null)
  const [ins, setIns] = useState<Insurance | null>(null)
  const [loans, setLoans] = useState<Loans | null>(null)
  const [selTail, setSelTail] = useState<string | null>(null)

  // The ops-sensitive slices reload after a settlement lands over the socket; the rest are load-once.
  const reloadOps = useCallback(() => {
    api.assignments().then(setAssignments).catch(() => {})
    api.hangar().then(setFleet).catch(() => {})
    api.finances().then(setFin).catch(() => {})
    api.flights().then(setFlights).catch(() => {})
    api.ledger(40).then(setLedger).catch(() => {})
    api.reputation().then(setRep).catch(() => {})
  }, [])

  useEffect(() => {
    api.assignments().then(setAssignments).catch(() => {})
    api.ranks().then(setRanks).catch(() => {})
    api.reputation().then(setRep).catch(() => {})
    api.hangar().then(setFleet).catch(() => {})
    api.finances().then(setFin).catch(() => {})
    api.bases().then(setBases).catch(() => {})
    api.flights().then(setFlights).catch(() => {})
    api.ledger(40).then(setLedger).catch(() => {})
    api.campaigns().then(setCampaigns).catch(() => {})
    api.challenges().then(setChallenges).catch(() => {})
    api.careerHighlights().then(setHighlights).catch(() => {})
    api.staff().then(setStaff).catch(() => {})
    api.routes().then(setRoutes).catch(() => {})
    api.insurance().then(setIns).catch(() => {})
    api.loans().then(setLoans).catch(() => {})
  }, [])

  // Claiming a challenge pays cash through the ledger, so refresh the board + the money-sensitive slices.
  const claimChallenge = useCallback(async (key: string) => {
    await api.claimChallenge(key)
    api.challenges().then(setChallenges).catch(() => {})
    api.finances().then(setFin).catch(() => {})
    api.ledger(40).then(setLedger).catch(() => {})
  }, [])

  // Live link to the sim — the same socket the Flight tab uses. Gives us the honest link badge AND the
  // aircraft's live position so it can move on the network map. Any settlement refreshes the ops slices.
  const { tele, wsOpen, link } = useTelemetry(reloadOps, () => {}, () => {})
  const live = link === 'Connected' && !!tele && (tele.lat !== 0 || tele.lon !== 0)

  const livery = airline?.identity.accentColorHex || '#6d84ff'

  // ── Derived readouts ───────────────────────────────────────────────────────
  const nw = fin?.netWorth ?? null
  const pnl = fin?.pnl ?? null
  const availCount = fleet.filter(f => f.availability === 'Available').length
  const routeCount = routes?.routes.length ?? 0
  const activeCampaign = campaigns.find(c => !c.completed) ?? null

  // Cash-balance curve reconstructed from the ledger window, anchored to end at current cash (same
  // technique the Logbook uses). Landing-quality trend from recent flights.
  const balances = (() => {
    const sorted = [...ledger].sort((a, b) => a.at.localeCompare(b.at))
    if (sorted.length < 2) return [] as number[]
    const net = sorted.reduce((s, e) => s + e.amountCents, 0)
    let running = state.cashCents - net
    return sorted.map(e => { running += e.amountCents; return Math.round(running / 100) })
  })()
  const fpms = [...flights].reverse().map(f => Math.round(f.touchdownFpm))

  // ── Alerts (all client-side flags already in hand) ─────────────────────────
  const alerts: DashAlert[] = []
  const cash = state.cashCents
  if (cash < 0) alerts.push({ level: 'bad', text: 'Your balance is negative — clear it before fees compound.', tab: 'finances', cta: 'Finances' })
  else if (cash < 1_000_000) alerts.push({ level: 'warn', text: 'Cash is running low.', tab: 'jobs', cta: 'Find work' })
  const grounded = fleet.filter(f => f.availability === 'Grounded').length
  if (grounded) alerts.push({ level: 'bad', text: `${grounded} aircraft grounded.`, tab: 'hangar', cta: 'Hangar' })
  const dueSvc = fleet.filter(f => f.maintenanceDue).length
  if (dueSvc) alerts.push({ level: 'warn', text: `${dueSvc} aircraft ${dueSvc === 1 ? 'needs' : 'need'} maintenance.`, tab: 'hangar', cta: 'Service' })
  const unrated = fleet.filter(f => !f.rated).length
  if (unrated) alerts.push({ level: 'warn', text: `You're not rated to fly ${unrated} owned aircraft.`, tab: 'flight', cta: 'Check-flight' })
  const uninsured = ins?.quotes.length ?? 0
  if (uninsured) alerts.push({ level: 'info', text: `${uninsured} aircraft uninsured.`, tab: 'finances', cta: 'Insure' })
  const outstanding = loans?.loans.reduce((s, l) => s + l.outstandingCents, 0) ?? 0
  if (outstanding > 0) alerts.push({ level: 'info', text: `Loans outstanding: ${money(outstanding)}.`, tab: 'finances', cta: 'Finances' })
  const repVal = state.reputationMilli / 1000
  if (repVal > 0 && repVal < 3) alerts.push({ level: 'warn', text: 'Reputation is low — clean landings rebuild it.', tab: 'jobs', cta: 'Fly clean' })
  if (fleet.length > 0 && assignments.length === 0) alerts.push({ level: 'info', text: 'No active job — the board is waiting.', tab: 'jobs', cta: 'Browse' })
  const sevOrder = { bad: 0, warn: 1, info: 2 } as const
  alerts.sort((a, b) => sevOrder[a.level] - sevOrder[b.level])

  const activeTail = selTail ?? fleet[0]?.tail ?? null
  const selAc = fleet.find(f => f.tail === activeTail) ?? null

  return (
    <div className="grid" style={{ ['--livery']: livery } as CSSProperties}>
      <AirlineHero state={state} airline={airline} wsOpen={wsOpen} link={link} tele={tele} live={live} />

      <DashCoach state={state} assignments={assignments} fleet={fleet} alerts={alerts} go={go} />

      {/* The few numbers that matter, at a glance. The rest live on their own tabs. */}
      <section className="hero-stats">
        <HeroStat label="Net worth" value={nw ? money(nw.netWorthCents) : '—'} accent tone={nw && nw.netWorthCents < 0 ? 'neg' : undefined} hint="assets − loans" onClick={() => go('finances')} />
        <HeroStat label="Cash" value={money(state.cashCents)} onClick={() => go('finances')} />
        <HeroStat label={`${pnl?.days ?? 30}-day P&L`} value={pnl ? money(pnl.netCents) : '—'} tone={pnl ? (pnl.netCents >= 0 ? 'pos' : 'neg') : undefined} onClick={() => go('finances')} />
        <HeroStat label="Fleet" value={String(fleet.length)} hint={`${availCount} available`} onClick={() => go('hangar')} />
        <HeroStat label="Reputation" value={repVal.toFixed(1)} />
        <HeroStat label="Experience" value={state.xp.toLocaleString()} unit="XP" />
      </section>

      {ranks.length > 0 && <RankCard state={state} ranks={ranks} />}

      <AlertsPanel alerts={alerts} go={go} />

      <div className="dash-cols">
        {/* Left column — the visual, image-forward half */}
        <div className="dash-col">
          <section className="card">
            <div className="row-head">
              <h2>Network</h2>
              <span className="hint">
                {bases.length} base{bases.length === 1 ? '' : 's'} · {fleet.length} airframe{fleet.length === 1 ? '' : 's'}
                {live && <> · <span className="pos">live</span></>}
              </span>
            </div>
            <NetworkMap bases={bases} fleet={fleet} assignments={assignments} tele={tele} link={link} selectedTail={activeTail} onSelect={setSelTail} />
          </section>

          <section className="card">
            <div className="row-head"><h2>Fleet</h2><button className="ghost small" onClick={() => go('hangar')}>Manage →</button></div>
            {fleet.length === 0
              ? <div className="empty"><p>No aircraft yet.</p><button className="primary" onClick={() => go('hangar')}>Visit the hangar</button></div>
              : <>
                  <FleetStrip fleet={fleet} selectedTail={activeTail} onSelect={setSelTail} />
                  {selAc && <FleetDetail a={selAc} go={go} />}
                </>}
          </section>

          {(balances.length > 1 || fpms.length > 1) && (
            <section className="card">
              <h2>Trends</h2>
              <div className="trends">
                <div className="trend-cell">
                  <div className="trend-head"><span className="metalabel">Cash balance</span><span className="num">{money(state.cashCents)}</span></div>
                  <Trendline values={balances} tone="accent" />
                </div>
                <div className="trend-cell">
                  <div className="trend-head"><span className="metalabel">Landing quality</span><span className="num">{fpms.length ? `${fpms[fpms.length - 1]} fpm` : '—'}</span></div>
                  <Trendline values={fpms} tone="pos" />
                </div>
              </div>
            </section>
          )}
        </div>

        {/* Right column — the operational readouts */}
        <div className="dash-col">
          <section className="card">
            <div className="row-head"><h2>Active assignments</h2>{assignments.length > 0 && <span className="hint">{assignments.length} accepted</span>}</div>
            {assignments.length === 0 ? (
              <div className="empty">
                <p>No job accepted yet.</p>
                <button className="primary" onClick={() => go('jobs')}>Browse jobs</button>
              </div>
            ) : (
              <ul className="assign-list">
                {assignments.map(a => {
                  const m = missionMeta(a.type)
                  return (
                    <li key={a.id} className="assign">
                      <div className="leg">
                        <span className="jrow-type" style={{ background: m.color }} title={m.label} />
                        <b>{a.origin}</b> → <b>{a.dest}</b> <span className="muted">{a.destName} · {a.commodity}</span>
                      </div>
                      <div className="assign-meta">
                        <span>{Math.round(a.distanceNm)} nm</span>
                        <span>{loadText(a.type, a.weightLbs, a.pax)}</span>
                        <span className="pos">{money(a.rewardQuoteCents)}</span>
                      </div>
                      <button className="primary small" onClick={() => go('flight')}>Fly →</button>
                    </li>
                  )
                })}
              </ul>
            )}
          </section>

          {challenges.some(c => c.done && !c.claimed) && <DashChallengesCard challenges={challenges} onClaim={claimChallenge} />}

          {/* Everything else about your operation — folded away by default so Home stays calm; one click to open. */}
          <details className="dash-more">
            <summary>More — campaign, challenges, finances, your record &amp; history</summary>
            <div className="dash-more-body">
              {challenges.length > 0 && !challenges.some(c => c.done && !c.claimed) && <DashChallengesCard challenges={challenges} onClaim={claimChallenge} />}
              {activeCampaign && <DashCampaignCard campaign={activeCampaign} go={go} />}
              {fin && <FinanceSnapshot fin={fin} go={go} />}
              {airline?.standing && <StandingBreakdown standing={airline.standing} color={livery} />}
              {rep && rep.events.length > 0 && <ReputationCard rep={rep} />}
              {highlights && highlights.totalFlights > 0 && <DashHighlightsCard h={highlights} />}
              <section className="card">
                <div className="row-head"><h2>Recent flights</h2><button className="ghost small" onClick={() => go('logbook')}>Logbook →</button></div>
                <RecentFlights flights={flights} />
              </section>
              <section className="card">
                <div className="row-head"><h2>Activity</h2><button className="ghost small" onClick={() => go('logbook')}>Ledger →</button></div>
                <ActivityFeed entries={ledger} />
              </section>
            </div>
          </details>
        </div>
      </div>

      {state.flights === 0 && (
        <section className="card how">
          <h2>How a leg works</h2>
          <ol>
            <li>Pick a job on the <b>Jobs</b> board — the reward is quoted and locked when you accept.</li>
            <li>Open <b>Flight</b>, begin the leg, and fly it in the sim.</li>
            <li>Land at the destination — Callsign settles the job automatically and pays you, itemized.</li>
          </ol>
          <p className="hint">Every dollar moves through the ledger, so the <b>Logbook</b> always reconciles with your cash.</p>
        </section>
      )}
    </div>
  )
}

// The airline hero — emblem + livery + standing over an ambient band, now with a live sim-link readout.
function AirlineHero({ state, airline, wsOpen, link, tele, live }: {
  state: State; airline: AirlineData | null; wsOpen: boolean; link: string; tele: Telemetry | null; live: boolean
}) {
  const id = airline?.identity
  const st = airline?.standing
  const color = id?.accentColorHex || '#6d84ff'
  const pct = st?.nextMove ? st.nextMove.progressPct : 100
  const badge = linkBadge(wsOpen, link)
  return (
    <section className="hero">
      <div className="hero-amb" aria-hidden="true">
        <svg viewBox="0 0 800 220" preserveAspectRatio="none">
          <path d="M0 70 C 150 40 300 100 460 62 S 720 30 800 66" />
          <path d="M0 120 C 160 92 320 150 500 108 S 730 78 800 116" />
          <path d="M0 172 C 140 148 340 196 520 158 S 740 132 800 166" />
        </svg>
      </div>
      <div className="hero-link" title="Live link to your simulator">
        <span className={`hlink-dot ${badge.tone}`} />
        <span className="hlink-text">{link === 'Connected' ? 'Sim linked' : badge.text}</span>
        {live && tele && <span className="hlink-live num">{Math.round(tele.alt).toLocaleString()} ft · {Math.round(tele.gs)} kt</span>}
      </div>
      <div className="hero-main">
        <div className="hero-badge"><Emblem emblem={id?.emblemKey || 'roundel'} color={color} size={60} /></div>
        <div className="hero-id">
          <div className="hero-name">{id?.name || 'Your airline'}</div>
          <div className="hero-sub">
            <span className="loc">{id?.tailCode || '—'}</span>
            <span className="dot-sep">•</span>
            <span>{state.name}</span>
            <span className="dot-sep">•</span>
            <span>{state.rank}</span>
            <span className="dot-sep">•</span>
            <span>Home <span className="loc">{state.homeIcao}</span></span>
            <span className="dot-sep">•</span>
            <span>At <span className="loc">{state.currentIcao}</span></span>
          </div>
        </div>
        {st && (
          <div className="hero-standing">
            <span className="tier-badge" style={{ background: `color-mix(in srgb, ${color} 18%, transparent)`, color }}>{st.stageName}</span>
            <div className="standing-bar"><div style={{ width: `${pct}%`, background: color }} /></div>
            <div className="standing-scale num">{st.nextMove ? `${st.nextMove.metCount} of ${st.nextMove.totalCount} for ${st.nextMove.stageName}` : 'Flag Carrier — top of the ladder'}</div>
          </div>
        )}
      </div>
    </section>
  )
}

// A stat tile. Optional tone colours the value (pos/neg), hint adds a sub-line, onClick makes it a
// navigable button. Backward-compatible with the plain label/value/accent/mono usage.
function HeroStat({ label, value, unit, accent, mono, tone, hint, onClick }: {
  label: string; value: string; unit?: string; accent?: boolean; mono?: boolean
  tone?: 'pos' | 'neg'; hint?: string; onClick?: () => void
}) {
  const inner = <>
    <div className="hs-label">{label}</div>
    <div className={`hs-value ${mono ? 'loc' : 'num'} ${tone ?? ''}`}>{value}{unit && <span className="hs-unit">{unit}</span>}</div>
    {hint && <div className="hs-hint">{hint}</div>}
  </>
  return onClick
    ? <button type="button" className={`hstat ${accent ? 'accent' : ''} clickable`} onClick={onClick}>{inner}</button>
    : <div className={`hstat ${accent ? 'accent' : ''}`}>{inner}</div>
}

// The operations-status panel: prominent alerts (grounded/maintenance/low cash/unrated/uninsured/…),
// or an "all clear" state when nothing needs attention. Every alert is one tap from where you fix it.
function AlertsPanel({ alerts, go }: { alerts: DashAlert[]; go: (t: Tab) => void }) {
  const clear = alerts.length === 0
  const worst = alerts.some(a => a.level === 'bad') ? 'bad' : 'warn'
  return (
    <section className="card alerts-card">
      <div className="row-head">
        <h2>Operations status</h2>
        <span className={`ops-pill ${clear ? 'ok' : worst}`}>{clear ? 'All clear' : `${alerts.length} to review`}</span>
      </div>
      {clear
        ? <div className="ops-clear"><span className="ops-check">✓</span> Nothing needs your attention — fair skies ahead.</div>
        : <ul className="alert-list">
            {alerts.map((al, i) => (
              <li key={i} className={`alert ${al.level}`}>
                <span className="alert-mark" />
                <span className="alert-text">{al.text}</span>
                {al.tab && <button className="alert-cta" onClick={() => go(al.tab!)}>{al.cta ?? 'Open'} →</button>}
              </li>
            ))}
          </ul>}
    </section>
  )
}

// The satellite network map: home + bases (accent), every owned airframe at its parking spot (coloured
// by availability, fanned out when they share a field), active-assignment legs (dashed), and the live
// aircraft (a plane marker that tracks in real time off telemetry). Click an airframe to drill into it.
function NetworkMap({ bases, fleet, assignments, tele, link, selectedTail, onSelect }: {
  bases: BaseView[]; fleet: OwnedAircraft[]; assignments: Assignment[]
  tele: Telemetry | null; link: string; selectedTail: string | null; onSelect: (tail: string) => void
}) {
  const host = useRef<HTMLDivElement>(null)
  const mapRef = useRef<L.Map | null>(null)
  const dataLayer = useRef<L.LayerGroup | null>(null)
  const planeRef = useRef<L.Marker | null>(null)
  const acMarkers = useRef<Record<string, { mk: L.CircleMarker; color: string }>>({})
  const onSelRef = useRef(onSelect); onSelRef.current = onSelect
  const online = typeof navigator === 'undefined' ? true : navigator.onLine

  const plottedBases = bases.filter(b => b.latitude !== 0 || b.longitude !== 0)
  const plottedFleet = fleet.filter(f => f.lat !== 0 || f.lon !== 0)
  // The map div only renders once there's something to plot; the init effect must re-run when that flips,
  // or the map is created against a null ref and never initialises (the dashboard-map black-box bug).
  const empty = plottedBases.length === 0 && plottedFleet.length === 0
  const legs = assignments.filter(a => (a.destLat !== 0 || a.destLon !== 0) && (a.originLat !== 0 || a.originLon !== 0))
  const sig = [
    ...plottedBases.map(b => `b:${b.icao}:${b.isHome ? 'h' : ''}`),
    ...plottedFleet.map(f => `a:${f.tail}@${f.locationIcao}:${f.availability}`),
    ...legs.map(a => `l:${a.origin}-${a.dest}`),
  ].join('|')

  useEffect(() => {
    if (!host.current || !online) return
    const map = L.map(host.current, { attributionControl: true, zoomControl: true, worldCopyJump: true }).setView([25, 0], 2)
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
      attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics', maxZoom: 18,
    }).addTo(map)
    dataLayer.current = L.layerGroup().addTo(map)
    mapRef.current = map
    const t = setTimeout(() => map.invalidateSize(), 60) // WebView2 flex layout can settle a beat late
    return () => { clearTimeout(t); map.remove(); mapRef.current = null; dataLayer.current = null; planeRef.current = null; acMarkers.current = {} }
  }, [online, empty])

  // (Re)draw bases, fleet, and active legs whenever the plotted data changes.
  useEffect(() => {
    const map = mapRef.current, layer = dataLayer.current
    if (!map || !layer) return
    layer.clearLayers(); acMarkers.current = {}

    for (const a of legs) {
      L.polyline([[a.originLat, a.originLon], [a.destLat, a.destLon]], { color: '#6d84ff', weight: 2, opacity: .55, dashArray: '6 8' }).addTo(layer)
      L.circleMarker([a.destLat, a.destLon], { radius: 4, weight: 1.5, color: '#6d84ff', fillColor: '#6d84ff', fillOpacity: .5 })
        .addTo(layer).bindTooltip(a.dest, { direction: 'top', className: 'sat-tip' })
    }
    for (const b of plottedBases) {
      if (b.isHome) L.circleMarker([b.latitude, b.longitude], { radius: 13, weight: 0, fillColor: '#6d84ff', fillOpacity: .14 }).addTo(layer)
      L.circleMarker([b.latitude, b.longitude], { radius: b.isHome ? 6 : 5, weight: 2, color: '#e9eef5', fillColor: '#6d84ff', fillOpacity: .9 })
        .addTo(layer).bindTooltip(`${b.icao}${b.isHome ? ' · home' : ''}`, { permanent: b.isHome, direction: 'right', className: 'sat-tip', offset: [6, 0] })
    }
    // Fan out airframes that share a field so they don't stack on one pixel.
    const seen: Record<string, number> = {}
    for (const f of plottedFleet) {
      const n = (seen[f.locationIcao] = (seen[f.locationIcao] ?? 0) + 1)
      const off = (n - 1) * 0.06
      const color = AVAIL_TONE[f.availability] ?? '#8a97a7'
      const mk = L.circleMarker([f.lat + off, f.lon + off], { radius: 5, weight: 1.5, color, fillColor: color, fillOpacity: .85 }).addTo(layer)
      mk.bindTooltip(`${f.tail} · ${f.name}`, { direction: 'top', className: 'sat-tip' })
      mk.on('click', () => onSelRef.current(f.tail))
      acMarkers.current[f.tail] = { mk, color }
    }

    const layers: L.Layer[] = []
    layer.eachLayer(l => layers.push(l))
    if (layers.length) {
      const bounds = L.featureGroup(layers).getBounds()
      if (bounds.isValid()) {
        // When everything sits at one field (e.g. a single base + its parked aircraft), the bounds are a
        // zero-area point — fitBounds then computes a broken zoom and the map renders black. Just centre it.
        if (bounds.getNorthEast().equals(bounds.getSouthWest())) map.setView(bounds.getCenter(), 9)
        else try { map.fitBounds(bounds.pad(0.3), { maxZoom: 8 }) } catch { map.setView(bounds.getCenter(), 8) }
      }
    }
    const t = setTimeout(() => map.invalidateSize(), 60)
    return () => clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, online])

  // The live aircraft — a plane marker driven straight off telemetry frames.
  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    // Only a genuinely AIRBORNE aircraft gets a live plane marker; a parked plane is already shown by its
    // fleet dot at its field, and the synthetic source's idle frames are zeroed server-side. This keeps a
    // phantom "flight" off the network map before a leg is actually flown.
    const isLive = link === 'Connected' && tele && !tele.onGround && (tele.lat !== 0 || tele.lon !== 0)
    if (!isLive) { if (planeRef.current) { planeRef.current.remove(); planeRef.current = null } return }
    const pos: [number, number] = [tele!.lat, tele!.lon]
    if (!planeRef.current) planeRef.current = L.marker(pos, { icon: planeIcon(0), interactive: false, zIndexOffset: 1000 }).addTo(map)
    else planeRef.current.setLatLng(pos)
  }, [tele, link])

  // Selection highlight.
  useEffect(() => {
    for (const [tail, { mk, color }] of Object.entries(acMarkers.current)) {
      const on = tail === selectedTail
      mk.setRadius(on ? 8 : 5)
      mk.setStyle({ weight: on ? 3 : 1.5, color: on ? '#ffffff' : color })
      if (on) mk.bringToFront()
    }
  }, [selectedTail, sig])

  if (!online) return <div className="empty" style={{ padding: 16 }}>The network map needs a connection for satellite imagery.</div>
  if (empty) return <div className="empty" style={{ padding: 16 }}>No mapped bases or aircraft yet.</div>
  return <div className="satmap dashmap" ref={host} role="img" aria-label="Network map" />
}

// The fleet carousel — a horizontally-scrolling strip of airframe chips (thumbnail, tail, location,
// worst-condition %). Selecting one drives the map highlight + the detail panel below.
function FleetStrip({ fleet, selectedTail, onSelect }: { fleet: OwnedAircraft[]; selectedTail: string | null; onSelect: (tail: string) => void }) {
  return (
    <div className="fleet-strip">
      {fleet.map(a => {
        const worst = Math.min(a.hullConditionMilli, a.engineConditionMilli)
        const tone = worst < 40000 ? 'neg' : worst < 70000 ? 'warn' : 'pos'
        return (
          <button key={a.id} type="button" className={`fleet-chip ${selectedTail === a.tail ? 'on' : ''}`} onClick={() => onSelect(a.tail)}>
            <AircraftImage typeId={a.typeId} category={a.category} mini />
            <div className="fc-body">
              <div className="fc-name">{a.name}</div>
              <div className="fc-tail loc">{a.tail}</div>
              <div className="fc-foot">
                <span className={`fc-dot ${AVAIL_KEY[a.availability] ?? ''}`} />
                <span className="muted loc">{a.locationIcao}</span>
                {a.maintenanceDue && <span className="warn-text">● svc</span>}
              </div>
            </div>
            <span className={`fc-cond num ${tone}`}>{Math.round(worst / 1000)}%</span>
          </button>
        )
      })}
    </div>
  )
}

// The drill-down for the selected airframe — full imagery, hull + engine rings, and the specs the
// backend now surfaces (seats, useful load, cruise, min runway), plus rating + next-service status.
function FleetDetail({ a, go }: { a: OwnedAircraft; go: (t: Tab) => void }) {
  const avail = a.availability === 'Available'
  return (
    <div className="fleet-detail">
      <AircraftImage typeId={a.typeId} category={a.category} />
      <div className="fd-head">
        <div>
          <div className="fd-name">{a.name}</div>
          <div className="fd-tail loc">{a.tail}<span className="muted"> · {spaced(a.category)}</span></div>
        </div>
        <span className={`avail-pill ${avail ? 'ok' : ''}`}>{spaced(a.availability)}</span>
      </div>
      <div className="fd-rings">
        <ConditionRing label="Hull" milli={a.hullConditionMilli} />
        <ConditionRing label="Engine" milli={a.engineConditionMilli} />
        <div className="fd-specs">
          <div><span className="metalabel">Based</span><span className="loc">{a.locationIcao}</span></div>
          <div><span className="metalabel">Airframe</span><span className="num">{a.airframeHours.toFixed(1)} h</span></div>
          <div><span className="metalabel">Seats</span><span className="num">{a.seats ?? '—'}</span></div>
          <div><span className="metalabel">Useful load</span><span className="num">{a.usefulLoadLbs ? `${a.usefulLoadLbs.toLocaleString()} lb` : '—'}</span></div>
          <div><span className="metalabel">Cruise</span><span className="num">{a.cruiseKtas ? `${a.cruiseKtas} kt` : '—'}</span></div>
          <div><span className="metalabel">Min rwy</span><span className="num">{a.minRunwayFt ? `${a.minRunwayFt.toLocaleString()} ft` : '—'}</span></div>
        </div>
      </div>
      <div className="fd-foot">
        {a.rated ? <span className="pos">Rated to fly</span> : <span className="warn-text" title={`Needs ${a.requiredClass}`}>● Not rated · {a.requiredClass}</span>}
        {a.maintenanceDue
          ? <button className="primary small" onClick={() => go('hangar')}>Service · {money(a.maintenanceQuoteCents)}</button>
          : <span className="muted num">Next service {money(a.maintenanceQuoteCents)}</span>}
      </div>
    </div>
  )
}

// A short "resets in …" for a period boundary, so the player feels the clock.
function resetsIn(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (ms <= 0) return 'resetting…'
  const h = Math.floor(ms / 3_600_000)
  if (h < 1) return `resets in ${Math.max(1, Math.floor(ms / 60_000))}m`
  if (h < 48) return `resets in ${h}h`
  return `resets in ${Math.floor(h / 24)}d`
}

// The rotating daily/weekly board — the retention hook. Each row is a period-delta goal with a claim button that
// lights up when it's genuinely met; claiming pays the reward once. Sits up front on the dashboard.
function DashChallengesCard({ challenges, onClaim }: { challenges: Challenge[]; onClaim: (key: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const claim = async (key: string) => { setBusy(key); try { await onClaim(key) } finally { setBusy(null) } }
  const daily = challenges.filter(c => c.cadence === 'Daily')
  const weekly = challenges.filter(c => c.cadence === 'Weekly')
  const claimable = challenges.filter(c => c.done && !c.claimed).length

  const group = (label: string, hint: string, items: Challenge[]) => items.length === 0 ? null : (
    <div className="chal-group">
      <div className="chal-group-head"><b>{label}</b><span className="hint">{hint}</span></div>
      {items.map(c => {
        const pct = c.target > 0 ? Math.min(100, (c.progress / c.target) * 100) : 0
        return (
          <div key={c.key} className={`chal-row ${c.claimed ? 'done' : ''}`}>
            <div className="chal-row-head">
              <b>{c.title}</b>
              <span className="num muted">{c.progress} / {c.target}</span>
            </div>
            <div className="chal-detail muted">{c.detail}</div>
            <div className="rank-bar"><div className="rank-fill" style={{ width: `${pct}%` }} /></div>
            <div className="chal-foot">
              <span className="num pos">{money(c.rewardCents)}</span>
              {c.claimed
                ? <span className="pill-done">Claimed ✓</span>
                : c.done
                  ? <button className="primary small" disabled={busy === c.key} onClick={() => claim(c.key)}>{busy === c.key ? 'Claiming…' : 'Claim reward'}</button>
                  : <span className="muted small">In progress</span>}
            </div>
          </div>
        )
      })}
    </div>
  )

  return (
    <section className="card">
      <div className="row-head">
        <h2>Challenges</h2>
        {claimable > 0 && <span className="pill-alert">{claimable} ready to claim</span>}
      </div>
      {group('Today', daily[0] ? resetsIn(daily[0].resetsAt) : '', daily)}
      {group('This week', weekly[0] ? resetsIn(weekly[0].resetsAt) : '', weekly)}
    </section>
  )
}

// The next-step coach (redesign R2/R4): names the single most useful action for the current state, so a
// newcomer always knows what to do — accept a job, fly it, fix what's grounding you, or take another.
function DashCoach({ state, assignments, fleet, alerts, go }: {
  state: State; assignments: Assignment[]; fleet: OwnedAircraft[]
  alerts: { level: string; text: string; tab: Tab; cta: string }[]; go: (t: Tab) => void
}) {
  const step: { text: string; cta: string; tab: Tab } = (() => {
    if (fleet.length === 0) return { text: 'Get an aircraft — then you can start flying for hire.', cta: 'Visit the hangar', tab: 'hangar' }
    if (assignments.length > 0) return { text: `You've accepted ${assignments.length === 1 ? 'a job' : `${assignments.length} jobs`} — open Flight and fly ${assignments.length === 1 ? 'it' : 'them'}.`, cta: 'Open Flight', tab: 'flight' }
    if (state.flights === 0) return { text: 'Accept your first job to begin — the reward is locked in when you accept.', cta: 'Browse jobs', tab: 'jobs' }
    const urgent = alerts.find(a => a.level === 'bad') ?? alerts.find(a => a.level === 'warn')
    if (urgent) return { text: urgent.text, cta: urgent.cta, tab: urgent.tab }
    return { text: "You're all set — take another job whenever you're ready.", cta: 'Browse jobs', tab: 'jobs' }
  })()
  return (
    <section className="dash-coach">
      <span className="dc-k">Next</span>
      <span className="dc-t">{step.text}</span>
      <button className="primary dc-cta" onClick={() => go(step.tab)}>{step.cta} →</button>
    </section>
  )
}

// "Your record" — career highlights straight off the flight log (NeoFly's Career highlights). A quick,
// satisfying read of what you've built: your workhorse aircraft, best paydays, total distance, best landing.
function DashHighlightsCard({ h }: { h: CareerHighlights }) {
  const hrs = Math.floor(h.blockMinutes / 60), mins = h.blockMinutes % 60
  const stat = (label: string, value: string, sub?: string) => (
    <div className="hl-stat">
      <div className="hl-value num">{value}</div>
      <div className="hl-label">{label}</div>
      {sub && <div className="hl-sub muted">{sub}</div>}
    </div>
  )
  return (
    <section className="card">
      <div className="row-head"><h2>Your record</h2><span className="hint">{h.totalFlights} flights · {hrs}h{String(mins).padStart(2, '0')}</span></div>
      <div className="hl-grid">
        {h.mostUsedAircraft && stat('Workhorse', h.mostUsedAircraft.title, `${h.mostUsedAircraft.count} legs`)}
        {stat('Best payday', money(h.bestRewardCents))}
        {stat('Best XP', `+${h.bestXp}`)}
        {stat('Distance flown', `${h.totalDistanceNm.toLocaleString()} nm`)}
        {h.smoothestFpm !== null && stat('Smoothest landing', `${signed(h.smoothestFpm)} fpm`)}
        {h.bestScore !== null && stat('Best score', `${h.bestScore}/100`)}
      </div>
    </section>
  )
}

// The furthest-along uncompleted campaign, with its current step's progress bar — a hook back into the story.
function DashCampaignCard({ campaign, go }: { campaign: Campaign; go: (t: Tab) => void }) {
  const step = campaign.steps[campaign.stepIndex] ?? campaign.steps[campaign.steps.length - 1]
  const pct = step && step.target > 0 ? Math.min(100, (step.progress / step.target) * 100) : 0
  return (
    <section className="card">
      <div className="row-head"><h2>Campaign</h2><span className="hint">step {Math.min(campaign.stepIndex + 1, campaign.stepCount)} / {campaign.stepCount}</span></div>
      <div className="camp-name">{campaign.name}</div>
      <p className="muted camp-desc">{campaign.description}</p>
      {step && (
        <div className="camp-step">
          <div className="camp-step-head"><b>{step.title}</b><span className="num muted">{step.progress} / {step.target}</span></div>
          <div className="rank-bar"><div className="rank-fill" style={{ width: `${pct}%` }} /></div>
          <div className="camp-detail muted">{step.detail}</div>
        </div>
      )}
      <div className="camp-foot">
        <span className="muted">Reward</span>
        <span className="num pos">{money(campaign.rewardCents)}</span>
        <button className="ghost small" onClick={() => go('campaigns')}>All campaigns →</button>
      </div>
    </section>
  )
}

// Net-worth composition + a compact 30-day cash-flow, reusing the finances bar primitives.
function FinanceSnapshot({ fin, go }: { fin: FinancesData; go: (t: Tab) => void }) {
  const nw = fin.netWorth
  const rows: { label: string; value: number; tone: 'accent' | 'neg' }[] = [
    { label: 'Cash', value: nw.cashCents, tone: 'accent' },
    { label: 'Aircraft', value: nw.aircraftCents, tone: 'accent' },
    { label: 'Inventory', value: nw.inventoryCents, tone: 'accent' },
    { label: 'Loans', value: -nw.loansCents, tone: 'neg' },
  ]
  const nwMax = Math.max(1, ...rows.map(r => Math.abs(r.value)))
  const pnlMax = Math.max(1, ...fin.pnl.lines.map(l => Math.abs(l.netCents)))
  return (
    <section className="card">
      <div className="row-head"><h2>Net worth</h2><span className={`num rep-score ${nw.netWorthCents >= 0 ? 'pos' : 'neg'}`}>{money(nw.netWorthCents)}</span></div>
      <div className="bars">{rows.map(r => <BarRow key={r.label} label={r.label} value={r.value} max={nwMax} tone={r.tone} />)}</div>
      {fin.pnl.lines.length > 0 && (
        <>
          <div className="row-head snap-sub"><h3 className="sub-h">Cash flow · {fin.pnl.days}d</h3><span className={`num ${fin.pnl.netCents >= 0 ? 'pos' : 'neg'}`}>{money(fin.pnl.netCents)}</span></div>
          <div className="bars">{fin.pnl.lines.slice(0, 5).map(l => <BarRow key={l.category} label={spaced(l.category)} value={l.netCents} max={pnlMax} tone={l.netCents >= 0 ? 'pos' : 'neg'} />)}</div>
        </>
      )}
      <button className="ghost small snap-btn" onClick={() => go('finances')}>Full finances →</button>
    </section>
  )
}

// The operating-score broken into its point contributions (the hero shows only the total + the stage).
function StandingBreakdown({ standing, color }: { standing: AirlineData['standing']; color: string }) {
  if (standing.contributions.length === 0) return null
  const max = Math.max(1, ...standing.contributions.map(c => c.points))
  return (
    <section className="card">
      <div className="row-head"><h2>Operating score</h2><span className="hint">{standing.stageName} · {standing.score} pts</span></div>
      <div className="bars">
        {standing.contributions.map(c => (
          <div className="barrow" key={c.label}>
            <span className="barrow-label">{c.label}</span>
            <div className="barrow-track"><div className="barrow-fill" style={{ width: `${Math.max(2, (c.points / max) * 100)}%`, background: color }} /></div>
            <span className="barrow-val num">{c.points}</span>
          </div>
        ))}
      </div>
    </section>
  )
}

// A compact recent-flights table.
function RecentFlights({ flights }: { flights: FlightLog[] }) {
  if (flights.length === 0) return <div className="empty">No flights logged yet.</div>
  return (
    <table className="tbl compact">
      <thead><tr><th>Aircraft</th><th className="r">Touchdown</th><th className="r">Payout</th><th className="r">When</th></tr></thead>
      <tbody>
        {flights.slice(0, 6).map(f => (
          <tr key={f.id}>
            <td className="ellip">{f.aircraftTitle}</td>
            <td className="r num">{signed(Math.round(f.touchdownFpm))} <span className="muted">{landingWord(f.touchdownFpm)}</span></td>
            <td className="r num pos">{money(f.payoutCents)}</td>
            <td className="r muted">{when(f.settledAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

// The live activity feed — the last dozen ledger movements, income green / spend red.
function ActivityFeed({ entries }: { entries: LedgerEntry[] }) {
  if (entries.length === 0) return <div className="empty">No activity yet.</div>
  return (
    <div className="feed">
      {entries.slice(0, 12).map((e, i) => (
        <div key={i} className={`feed-row ${e.amountCents >= 0 ? 'pos' : 'neg'}`}>
          <span className="feed-mark" />
          <span className="feed-desc">{e.description}<span className="muted feed-cat"> · {spaced(e.category)}</span></span>
          <span className={`feed-amt num ${e.amountCents >= 0 ? 'pos' : 'neg'}`}>{money(e.amountCents)}</span>
          <span className="feed-when muted">{when(e.at)}</span>
        </div>
      ))}
    </div>
  )
}

// ─── Jobs ────────────────────────────────────────────────────────────────────

// Per-mission identity: an accent colour + label for the illustrated card header, keyed by the enum name
// the API sends (Cargo, Vip, SearchAndRescue, …). Unknown types fall back to the app accent.
const MISSION_META: Record<string, { color: string; label: string }> = {
  Cargo: { color: '#6d84ff', label: 'Cargo' },
  Passenger: { color: '#3ecf8e', label: 'Passenger' },
  Express: { color: '#e0912f', label: 'Express' },
  Sensitive: { color: '#8b7be8', label: 'Sensitive' },
  Hazardous: { color: '#d9a11c', label: 'Hazardous' },
  Emergency: { color: '#f26a5c', label: 'Emergency' },
  SearchAndRescue: { color: '#f0824c', label: 'Search & Rescue' },
  Tourist: { color: '#39b56a', label: 'Tourist' },
  Parachute: { color: '#2bb6c4', label: 'Parachute' },
  Vip: { color: '#d9b84a', label: 'VIP' },
  Illicit: { color: '#a06bd6', label: 'Illicit' },
}
function missionMeta(type: string) { return MISSION_META[type] ?? { color: 'var(--accent)', label: spaced(type) } }

// Original stroke icons per mission type — the "illustration" on each mission card.
function missionIcon(type: string) {
  switch (type) {
    case 'Cargo': return <><path d="M3 8l9-4 9 4v8l-9 4-9-4z" /><path d="M3 8l9 4 9-4M12 12v8" /></>
    case 'Passenger': return <><circle cx="9" cy="8" r="3" /><path d="M3.5 20c0-3 2.5-5 5.5-5s5.5 2 5.5 5" /><path d="M16 6a3 3 0 0 1 0 6" /></>
    case 'Express': return <path d="M13 2L4 14h6l-1 8 9-12h-6z" />
    case 'Sensitive': return <><path d="M12 3l7 3v5c0 5-3 8-7 10-4-2-7-5-7-10V6z" /><path d="M12 10.5v3.5" /></>
    case 'Hazardous': return <><path d="M12 4l9 16H3z" /><path d="M12 10v4M12 17.2h0" /></>
    case 'Emergency': return <path d="M10 3h4v5h5v4h-5v5h-4v-5H5V8h5z" />
    case 'SearchAndRescue': return <><circle cx="12" cy="12" r="8.5" /><circle cx="12" cy="12" r="3.2" /><path d="M12 3.5v5M12 15.5v5M3.5 12h5M15.5 12h5" /></>
    case 'Tourist': return <><rect x="3" y="7" width="18" height="12" rx="2" /><circle cx="12" cy="13" r="3" /><path d="M8.5 7l1.2-2h4.6l1.2 2" /></>
    case 'Parachute': return <><path d="M3 11a9 9 0 0 1 18 0z" /><path d="M3 11l9 6 9-6M9.5 11l2.5 6 2.5-6" /></>
    case 'Vip': return <path d="M12 3l2.5 6 6.5.5-5 4.3 1.6 6.2-5.6-3.4-5.6 3.4 1.6-6.2-5-4.3 6.5-.5z" />
    case 'Illicit': return <><path d="M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12z" /><circle cx="12" cy="12" r="2.6" /></>
    default: return <path d="M4 13l16-6-6 16-2-7-8-3z" />
  }
}

type JobSort = 'dist' | 'reward' | 'xp' | 'weight'
function sortMark(cur: string, key: string, asc: boolean): string { return cur === key ? (asc ? ' ↑' : ' ↓') : '' }
// A keyboard-accessible sortable column header: clickable AND operable with Enter/Space, with aria-sort so
// screen readers announce the current sort. Replaces bare <th onClick> which the mouse-only could use.
function SortTh<K extends string>({ label, k, sort, asc, onSort }: { label: string; k: K; sort: string; asc: boolean; onSort: (k: K) => void }) {
  return (
    <th className="r sortable" role="button" tabIndex={0}
        aria-sort={sort === k ? (asc ? 'ascending' : 'descending') : 'none'}
        onClick={() => onSort(k)}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSort(k) } }}>
      {label}{sortMark(sort, k, asc)}
    </th>
  )
}
function deadline(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (ms <= 0) return 'expired'
  const h = Math.floor(ms / 3.6e6), min = Math.floor((ms % 3.6e6) / 6e4)
  return h > 0 ? `${h}h ${min}m` : `${min}m`
}

function Jobs({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [jobs, setJobs] = useState<Job[] | null>(null)
  const [staff, setStaff] = useState<Staff[]>([])
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [dispatches, setDispatches] = useState<DispatchLeg[]>([])
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()
  const [selected, setSelected] = useState<string | null>(null)
  const [types, setTypes] = useState<Set<string>>(new Set()) // empty = all types shown
  const [maxDist, setMaxDist] = useState<number>(Infinity)
  const [maxWeight, setMaxWeight] = useState<number>(Infinity)
  const [maxPax, setMaxPax] = useState<number>(Infinity)
  const [client, setClient] = useState<string>('') // '' = every client (Phase 12 — grind one client's loyalty)
  const [fitOnly, setFitOnly] = useState(true) // hide jobs no owned aircraft can carry (seats / useful load)
  const [sort, setSort] = useState<JobSort>('dist')
  const [asc, setAsc] = useState(true)

  const load = useCallback(async () => {
    try {
      setJobs(await api.jobs())
      setStaff(await api.staff())      // Phase 12 — for dispatching a hired crew to fly a job
      setFleet(await api.hangar())
      setDispatches(await api.dispatches()) // to offer appending a leg to a crew's itinerary
    } catch (e) { setMsg(String(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const refresh = async () => {
    setBusy(true); setMsg(null)
    try { await api.refreshJobs(12); await load(); setSelected(null) } catch (e) { setMsg(String(e)) } finally { setBusy(false) }
  }
  const accept = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.accept(id); await load(); onChanged(); setMsg('Accepted — head to the Flight tab to fly it.'); setSelected(null) }
    catch (e) { setMsg(String(e)) } finally { setBusy(false) }
  }
  const dispatch = async (jobId: string, staffId: string, aircraftId: string) => {
    setBusy(true); setMsg(null)
    try { await api.dispatchJob(jobId, staffId, aircraftId); await load(); onChanged(); setMsg('Dispatched — the crew flies it autonomously. Bank it from the Staff tab (Process now).'); setSelected(null) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const all = jobs ?? []
  const distMax = Math.max(100, ...all.map(j => Math.ceil(j.distanceNm)))
  const wtMax = Math.max(100, ...all.map(j => j.weightLbs))
  const paxMax = Math.max(1, ...all.map(j => j.pax))
  const hasPax = all.some(j => j.pax > 0)
  const kinds = Array.from(new Set(all.map(j => j.type)))
  const clients = Array.from(new Set(all.map(j => j.clientName).filter((n): n is string => !!n))).sort()
  // What the fleet can actually carry — a passenger charter needs seats, cargo needs useful load
  // (mirrors the begin-flight gate). Used to hide/flag jobs no owned aircraft could fly.
  const fleetMaxSeats = Math.max(0, ...fleet.map(f => f.seats ?? 0))
  const fleetMaxLoad = Math.max(0, ...fleet.map(f => f.usefulLoadLbs ?? 0))
  const doable = (j: Job) => fleet.length === 0 || (j.pax > 0 ? fleetMaxSeats >= j.pax : fleetMaxLoad >= j.weightLbs)
  const key = (j: Job) => sort === 'reward' ? j.rewardCents : sort === 'xp' ? j.xp : sort === 'weight' ? j.weightLbs : j.distanceNm
  const shown = all
    .filter(j => types.size === 0 || types.has(j.type))
    .filter(j => maxDist === Infinity || j.distanceNm <= maxDist)
    .filter(j => maxWeight === Infinity || j.weightLbs <= maxWeight)
    .filter(j => maxPax === Infinity || j.pax <= maxPax)
    .filter(j => !client || j.clientName === client)
    .filter(j => !fitOnly || doable(j))
    .sort((a, b) => (key(a) - key(b)) * (asc ? 1 : -1))
  const hiddenUnfit = fitOnly ? all.filter(j => !doable(j)).length : 0
  const sel = shown.find(j => j.id === selected) ?? null

  const toggleType = (t: string) => setTypes(s => { const n = new Set(s); n.has(t) ? n.delete(t) : n.add(t); return n })
  const setSortKey = (k: JobSort) => { if (sort === k) setAsc(a => !a); else { setSort(k); setAsc(k === 'dist') } }

  return (
    <div className="jobs-screen">
      <div className="row-head">
        <h2>Jobs from <span className="loc">{state.currentIcao}</span> <span className="muted">· {shown.length} of {all.length}</span></h2>
        <button className="primary" disabled={busy} onClick={refresh}>{busy ? '…' : 'Refresh board'}</button>
      </div>
      {all.length > 0 && (
        <div className="hero-stats tab-summary">
          <HeroStat label="On the board" value={String(all.length)} accent />
          <HeroStat label="Best reward" value={money(Math.max(...all.map(j => j.rewardCents)))} tone="pos" />
          <HeroStat label="Top XP" value={`+${Math.max(...all.map(j => j.xp))}`} />
          <HeroStat label="Nearest" value={String(Math.round(Math.min(...all.map(j => j.distanceNm))))} unit="nm" />
          {clients.length > 0 && <HeroStat label="Clients hiring" value={String(clients.length)} />}
        </div>
      )}
            {jobs === null ? <div className="empty">Loading…</div>
        : all.length === 0 ? <div className="empty"><p>No jobs on the board.</p><button className="primary" onClick={refresh}>Generate jobs</button></div>
          : (
            <>
              <div className="job-filters">
                <div className="jf-types">
                  {kinds.map(t => {
                    const m = missionMeta(t); const on = types.size === 0 || types.has(t)
                    return (
                      <button key={t} type="button" className={`jf-type ${on ? 'on' : ''}`} style={on ? { borderColor: m.color, color: m.color } : undefined} onClick={() => toggleType(t)}>
                        <svg viewBox="0 0 24 24">{missionIcon(t)}</svg>{m.label}
                      </button>
                    )
                  })}
                </div>
                <div className="jf-sliders">
                  <label>Max distance <b className="num">{maxDist === Infinity ? `any` : `${Math.round(maxDist)} nm`}</b>
                    <input type="range" min={50} max={distMax} value={maxDist === Infinity ? distMax : maxDist} onChange={e => { const v = Number(e.target.value); setMaxDist(v >= distMax ? Infinity : v) }} />
                  </label>
                  <label>Max payload <b className="num">{maxWeight === Infinity ? `any` : `${Math.round(maxWeight).toLocaleString()} lb`}</b>
                    <input type="range" min={0} max={wtMax} step={100} value={maxWeight === Infinity ? wtMax : maxWeight} onChange={e => { const v = Number(e.target.value); setMaxWeight(v >= wtMax ? Infinity : v) }} />
                  </label>
                  {hasPax && (
                    <label>Max pax <b className="num">{maxPax === Infinity ? `any` : maxPax}</b>
                      <input type="range" min={0} max={paxMax} value={maxPax === Infinity ? paxMax : maxPax} onChange={e => { const v = Number(e.target.value); setMaxPax(v >= paxMax ? Infinity : v) }} />
                    </label>
                  )}
                  {clients.length > 1 && (
                    <label className="jf-client">Client
                      <select value={client} onChange={e => setClient(e.target.value)}>
                        <option value="">All clients</option>
                        {clients.map(c => <option key={c} value={c}>{c}</option>)}
                      </select>
                    </label>
                  )}
                  {fleet.length > 0 && (
                    <button type="button" className={`jf-fit ${fitOnly ? 'on' : ''}`} onClick={() => setFitOnly(v => !v)}
                      title="Only show jobs an aircraft you own can actually carry">
                      {fitOnly ? `✓ My fleet can fly it${hiddenUnfit > 0 ? ` · ${hiddenUnfit} hidden` : ''}` : 'Show all jobs'}
                    </button>
                  )}
                </div>
              </div>

              <div className="jobs-work">
                <div className="jobs-tablewrap">
                  <table className="tbl jobs-table">
                    <thead><tr>
                      <th>Destination</th>
                      <SortTh label="Dist" k="dist" sort={sort} asc={asc} onSort={setSortKey} />
                      <SortTh label="Load" k="weight" sort={sort} asc={asc} onSort={setSortKey} />
                      <SortTh label="Reward" k="reward" sort={sort} asc={asc} onSort={setSortKey} />
                      <SortTh label="XP" k="xp" sort={sort} asc={asc} onSort={setSortKey} />
                    </tr></thead>
                    <tbody>
                      {shown.map(j => {
                        const m = missionMeta(j.type)
                        const unfit = !doable(j)
                        return (
                          <tr key={j.id} className={`jrow ${selected === j.id ? 'on' : ''} ${j.locked ? 'locked' : ''} ${unfit ? 'unfit' : ''}`} onClick={() => setSelected(j.id)}>
                            <td>
                              <span className="jrow-type" style={{ background: m.color }} title={m.label} /><span className="loc">{j.dest}</span> <span className="muted">{j.destName}</span>
                              {unfit && <span className="unfit-badge" title={j.pax > 0 ? `Needs ${j.pax} seats — your biggest aircraft has ${fleetMaxSeats}` : `Needs ${j.weightLbs.toLocaleString()} lb — your biggest aircraft carries ${fleetMaxLoad.toLocaleString()}`}>too big for your fleet</span>}
                              {j.clientName && <div className="jrow-client">{j.clientName}{j.clientLoyaltyMilli >= 25000 && <span className="loyal-star" title={`Loyal client · ${Math.round(j.clientLoyaltyMilli / 1000)}%`}> ★</span>}</div>}
                            </td>
                            <td className="r num">{Math.round(j.distanceNm)}</td>
                            <td className="r num">{isPaxType(j.type) ? `${j.pax}p` : `${(j.weightLbs / 1000).toFixed(1)}k`}</td>
                            <td className="r num pos">{money(j.rewardCents)}{j.expectedLoyaltyBonusCents > 0 && <span className="jrow-bonus" title="Loyal client repeat premium"> +{money(j.expectedLoyaltyBonusCents)}</span>}{j.hubRepBonusCents != null && j.hubRepBonusCents > 0 && <span className="jrow-hub" title="Your operating reputation lifted the pay at this hub"> ⌂+{money(j.hubRepBonusCents)}</span>}</td>
                            <td className="r num">+{j.xp}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
                <div className="jobs-side">
                  {sel ? <JobDetail job={sel} busy={busy} onAccept={accept} staff={staff} fleet={fleet} dispatches={dispatches} onDispatch={dispatch} /> : <div className="card jobs-pick"><div className="empty">Select a job for the full briefing.</div></div>}
                  <JobsMap jobs={shown} selectedId={selected} onSelect={setSelected} hereLabel={`You · ${state.currentIcao}`} />
                </div>
              </div>
            </>
          )}
    </div>
  )
}

// The full briefing for one job — objective, geography, arrival detail, and the net after the landing fee.
// Your standing with a client (Phase 8d), tiered off loyalty milli. 25% is the premium threshold — below it
// a client is still "New" and pays no repeat bonus.
function loyaltyTier(milli: number): { label: string; on: boolean } {
  const pct = milli / 1000
  if (pct >= 80) return { label: 'Preferred', on: true }
  if (pct >= 50) return { label: 'Loyal', on: true }
  if (pct >= 25) return { label: 'Regular', on: true }
  return { label: 'New client', on: false }
}

// The Clients tab (Phase 8d-2): the face of the client system — who you fly for and where each bond stands.
function Clients() {
  const [clients, setClients] = useState<Client[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => { api.clients().then(setClients).catch(e => setErr(cleanErr(e))) }, [])

  if (err) return <div className="empty">{err}</div>
  if (!clients) return <div className="empty">Loading…</div>
  if (clients.length === 0) return (
    <div className="card">
      <h2>Clients</h2>
      <p className="hint">You haven't built any client relationships yet. Every job on the board is offered by a client — complete their work well and their loyalty grows, paying a repeat premium on future jobs. Fail them and they cool off.</p>
    </div>
  )
  return (
    <div className="card">
      <div className="row-head"><h2>Your clients</h2><span className="muted">{clients.length} relationship{clients.length === 1 ? '' : 's'}</span></div>
      <p className="hint">Serve a client well and they pay a repeat premium on their jobs; neglect them and the bond cools. Loyalty shown is current — it decays while you don't fly for them.</p>
      <div className="clients-tablewrap">
        <table className="tbl clients-table">
          <thead><tr><th>Client</th><th>Home</th><th>Standing</th><th className="r">Delivered</th><th className="r">Failed</th><th className="r">Last job</th></tr></thead>
          <tbody>
            {clients.map(c => {
              const tier = loyaltyTier(c.loyaltyMilli)
              return (
                <tr key={c.name + c.homeIcao}>
                  <td><b>{c.name}</b></td>
                  <td><span className="loc">{c.homeIcao}</span></td>
                  <td><span className={`loyal-tag${tier.on ? ' on' : ''}`}>{tier.label}</span> <span className="num muted"> {Math.round(c.loyaltyMilli / 1000)}%</span></td>
                  <td className="r num pos">{c.jobsCompleted}</td>
                  <td className="r num">{c.jobsFailed > 0 ? <span className="neg">{c.jobsFailed}</span> : '—'}</td>
                  <td className="r muted">{c.lastJobAt ? new Date(c.lastJobAt).toLocaleDateString() : '—'}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function JobDetail({ job, busy, onAccept, staff, fleet, dispatches, onDispatch }: {
  job: Job; busy: boolean; onAccept: (id: string) => void
  staff: Staff[]; fleet: OwnedAircraft[]; dispatches: DispatchLeg[]
  onDispatch: (jobId: string, staffId: string, aircraftId: string) => void
}) {
  const [dStaff, setDStaff] = useState('')
  const [dAircraft, setDAircraft] = useState('')
  // Phase 12 — a hired crew + an aircraft, BOTH co-located at the job's origin, can fly it autonomously.
  const eligibleCrew = staff.filter(s => s.role !== 'Manager' && !s.flying && (!s.currentIcao || s.currentIcao === job.origin))
  const eligibleAircraft = fleet.filter(f => f.availability === 'Available' && f.locationIcao === job.origin)
  const canDispatch = !job.locked && eligibleCrew.length > 0 && eligibleAircraft.length > 0
  // Crews already mid-itinerary whose run ENDS at this job's origin (and has room for another leg, max 3) can
  // append it — a connected one-way chain. One click: their existing tail continues on.
  const byCrew = new Map<string, DispatchLeg[]>()
  dispatches.forEach(d => byCrew.set(d.staffId, [...(byCrew.get(d.staffId) ?? []), d]))
  const continuable = job.locked ? [] : [...byCrew.values()]
    .filter(legs => legs.length < 3)
    .map(legs => [...legs].sort((a, b) => a.readyAt < b.readyAt ? -1 : 1).at(-1)!)
    .filter(last => last.dest === job.origin)
  const m = missionMeta(job.type)
  const geo = (job.originLat || job.originLon) && (job.destLat || job.destLon)
  const hdg = geo ? Math.round(bearing([job.originLat, job.originLon], [job.destLat, job.destLon])) : null
  const tier = loyaltyTier(job.clientLoyaltyMilli)
  const net = job.rewardCents + job.expectedLoyaltyBonusCents - job.expectedLandingFeeCents
  return (
    <div className="card jdetail">
      <div className="mission-head">
        <span className="mission-badge" style={{ background: `color-mix(in srgb, ${m.color} 16%, transparent)`, color: m.color }}><svg viewBox="0 0 24 24">{missionIcon(job.type)}</svg></span>
        <div className="mission-title">
          <div className="mission-type">{m.label}</div>
          <div className="mission-route"><b>{job.origin}</b> <span className="arrow">→</span> <b>{job.dest}</b></div>
        </div>
      </div>
      <div className="jd-dest">{job.destName} <span className="muted">· {spaced(job.destKind).replace(' Airport', '')}</span></div>
      {job.clientName && <div className="jd-client">
        <span className="muted">Client</span> <b>{job.clientName}</b>
        <span className={`loyal-tag${tier.on ? ' on' : ''}`} title={`Loyalty ${Math.round(job.clientLoyaltyMilli / 1000)}%`}>{tier.label}{tier.on ? ` · ${Math.round(job.clientLoyaltyMilli / 1000)}%` : ''}</span>
      </div>}
      <p className="jd-obj">{isPaxType(job.type) ? `Carry ${job.pax} ${job.pax === 1 ? 'passenger' : 'passengers'}` : `Haul ${job.weightLbs.toLocaleString()} lb of ${job.commodity.toLowerCase()}`} to {job.dest}.</p>
      <div className="jd-grid">
        <div><span className="metalabel">Distance</span><span className="num">{Math.round(job.distanceNm)} nm</span></div>
        <div><span className="metalabel">{isPaxType(job.type) ? 'Passengers' : 'Payload'}</span><span className="num">{loadText(job.type, job.weightLbs, job.pax)}</span></div>
        {hdg !== null && <div><span className="metalabel">Bearing</span><span className="num">{String(hdg).padStart(3, '0')}°</span></div>}
        <div><span className="metalabel">Longest rwy</span><span className="num">{job.destLongestRunwayFt ? `${job.destLongestRunwayFt.toLocaleString()} ft` : '—'}</span></div>
        <div><span className="metalabel">XP</span><span className="num">+{job.xp}</span></div>
        <div><span className="metalabel">Load by</span><span className="num">{deadline(job.expiresAt)}</span></div>
      </div>
      <div className="jd-pay">
        <div className="jd-payrow"><span>Reward</span><span className="num pos">{money(job.rewardCents)}</span></div>
        {job.hubRepBonusCents != null && job.hubRepBonusCents > 0 && <div className="jd-payrow jd-sub"><span className="muted">⌂ Your reputation lifts this hub</span><span className="num pos">incl. +{money(job.hubRepBonusCents)}</span></div>}
        {job.expectedLoyaltyBonusCents > 0 && <div className="jd-payrow"><span className="muted">Loyal client bonus</span><span className="num pos">+{money(job.expectedLoyaltyBonusCents)}</span></div>}
        <div className="jd-payrow"><span className="muted">Est. landing fee</span><span className="num neg">-{money(job.expectedLandingFeeCents)}</span></div>
        <div className="jd-payrow jd-net"><span>Net</span><span className="num">{money(net)}</span></div>
      </div>
      {job.locked
        ? <div className="banner warn">🔒 {job.lockReason}</div>
        : <button className="primary jd-accept" disabled={busy} onClick={() => onAccept(job.id)}>Accept this job</button>}
      {(canDispatch || continuable.length > 0) && (
        <div className="jd-dispatch">
          <div className="metalabel">Or dispatch a hired crew — they fly it while you're away (one-way)</div>
          {canDispatch && (
            <div className="dispatch-form">
              <select value={dStaff} onChange={e => setDStaff(e.target.value)}><option value="">Crew…</option>{eligibleCrew.map(s => <option key={s.id} value={s.id}>{s.name} · {Math.round(s.skillMilli / 1000)}%</option>)}</select>
              <select value={dAircraft} onChange={e => setDAircraft(e.target.value)}><option value="">Aircraft…</option>{eligibleAircraft.map(f => <option key={f.id} value={f.id}>{f.name} · {f.tail}{f.ownership === 'Rented' ? ' (rental)' : ''}</option>)}</select>
              <button className="ghost" disabled={busy || !dStaff || !dAircraft} onClick={() => onDispatch(job.id, dStaff, dAircraft)}>Dispatch</button>
            </div>
          )}
          {continuable.map(c => (
            <button key={c.staffId} className="ghost jd-append" disabled={busy} onClick={() => onDispatch(job.id, c.staffId, c.aircraftInstanceId)}>
              + Add to {c.crewName}'s run · {c.tail}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

// Every shown job plotted at its destination, coloured by mission type, clickable to select.
function JobsMap({ jobs, selectedId, onSelect, hereLabel }: { jobs: Job[]; selectedId: string | null; onSelect: (id: string) => void; hereLabel?: string }) {
  const host = useRef<HTMLDivElement>(null)
  const mapRef = useRef<L.Map | null>(null)
  const markers = useRef<Record<string, { mk: L.CircleMarker; color: string }>>({})
  const routeLine = useRef<L.Polyline | null>(null)
  const online = typeof navigator === 'undefined' ? true : navigator.onLine
  const plotted = jobs.filter(j => j.destLat !== 0 || j.destLon !== 0)
  // Every board job departs where you (or the selected crew) are now, so any job's origin is "here".
  const hereJob = plotted.find(j => j.originLat !== 0 || j.originLon !== 0)
  const here: [number, number] | null = hereJob ? [hereJob.originLat, hereJob.originLon] : null
  const sig = plotted.map(j => j.id).join('|')
  const onSelRef = useRef(onSelect); onSelRef.current = onSelect

  useEffect(() => {
    if (!host.current || !online || plotted.length === 0) return
    const map = L.map(host.current, { attributionControl: true, zoomControl: true, worldCopyJump: true })
    mapRef.current = map
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', { attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics', maxZoom: 18 }).addTo(map)
    markers.current = {}
    routeLine.current = L.polyline([], { color: '#6d84ff', weight: 2, opacity: .8, dashArray: '6 8' }).addTo(map)
    const mks = plotted.map(j => {
      const m = missionMeta(j.type)
      const mk = L.circleMarker([j.destLat, j.destLon], { radius: 5, weight: 1.5, color: m.color, fillColor: m.color, fillOpacity: .85 }).addTo(map)
      mk.bindTooltip(`${j.dest} · ${money(j.rewardCents)}`, { direction: 'top', className: 'sat-tip' })
      mk.on('click', () => onSelRef.current(j.id))
      markers.current[j.id] = { mk, color: m.color }
      return mk
    })
    // A distinct "you are here" pin at the departure field.
    if (here) L.circleMarker(here, { radius: 6, weight: 2, color: '#ffffff', fillColor: '#2ea3ff', fillOpacity: 1 })
      .addTo(map).bindTooltip(hereLabel ?? 'You are here', { direction: 'top', className: 'sat-tip' })
    const group = L.featureGroup(here ? [...mks, L.circleMarker(here)] : mks)
    map.fitBounds(group.getBounds().pad(0.3), { maxZoom: 8 })
    const t = setTimeout(() => map.invalidateSize(), 60)
    return () => { clearTimeout(t); map.remove(); mapRef.current = null; markers.current = {}; routeLine.current = null }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, online])

  useEffect(() => {
    for (const [id, { mk, color }] of Object.entries(markers.current)) {
      const on = id === selectedId
      mk.setRadius(on ? 8 : 5)
      mk.setStyle({ weight: on ? 3 : 1.5, color: on ? '#ffffff' : color })
      if (on) mk.bringToFront()
    }
    // Draw a line from here to the selected job's destination (cleared when nothing's selected).
    const sel = plotted.find(j => j.id === selectedId)
    if (routeLine.current) routeLine.current.setLatLngs(sel && here ? [here, [sel.destLat, sel.destLon]] : [])
  }, [selectedId, sig])

  if (!online) return <div className="card"><div className="empty" style={{ padding: 16 }}>Map needs a connection.</div></div>
  if (plotted.length === 0) return <div className="card"><div className="empty" style={{ padding: 16 }}>No mapped destinations.</div></div>
  return <div className="satmap jobsmap" ref={host} role="img" aria-label="Jobs map" />
}

function RankCard({ state, ranks }: { state: State; ranks: RankTier[] }) {
  const current = ranks.find(r => r.current) ?? ranks[0]
  const next = ranks.find(r => !r.reached) // tiers are ascending; first not-yet-reached is the next goal
  const pct = next && next.minXp > current.minXp
    ? Math.max(0, Math.min(100, ((state.xp - current.minXp) / (next.minXp - current.minXp)) * 100))
    : 100
  return (
    <section className="card rank-card">
      <div className="row-head">
        <h2>{current.displayName}</h2>
        <span className="hint">{next ? `${(next.minXp - state.xp).toLocaleString()} XP to ${next.displayName}` : 'Top rank reached'}</span>
      </div>
      <p className="muted rank-desc">{current.description}</p>
      <div className="rank-bar"><div className="rank-fill" style={{ width: `${pct}%` }} /></div>
      <div className="rank-scale"><span className="num">{state.xp.toLocaleString()} XP</span><span className="num">{next ? `${next.minXp.toLocaleString()} XP` : ''}</span></div>
    </section>
  )
}

function ReputationCard({ rep }: { rep: Reputation }) {
  return (
    <section className="card">
      <div className="row-head"><h2>Reputation</h2><span className="num rep-score">{(rep.reputationMilli / 1000).toFixed(1)}</span></div>
      <ul className="rep-log">
        {rep.events.map((e, i) => (
          <li key={i}>
            <span className={`num ${e.deltaMilli >= 0 ? 'pos' : 'neg'}`}>{e.deltaMilli >= 0 ? '+' : ''}{(e.deltaMilli / 1000).toFixed(2)}</span>
            <span className="rep-reason">{e.reason}</span>
            <span className="muted num">{(e.balanceMilli / 1000).toFixed(1)}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

function Meta({ label, value }: { label: string; value: string }) {
  return <div className="metacell"><span className="metalabel">{label}</span><span className="num">{value}</span></div>
}

// ─── Flight (live HUD + settlement) ──────────────────────────────────────────

function useTelemetry(onSettled: (s: Settled) => void, onDiverted: (d: Diverted) => void, onCheckFlight: (c: CheckFlightDone) => void, onEvent?: (e: LiveEvent) => void, onFreeFlight?: (f: { flightId: string; touchdownFpm: number; overallScore: number | null }) => void) {
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [wsOpen, setWsOpen] = useState(false)
  const [link, setLink] = useState('Disconnected') // SimConnectionState from the server
  const cb = useRef(onSettled)
  cb.current = onSettled
  const dcb = useRef(onDiverted)
  dcb.current = onDiverted
  const ccb = useRef(onCheckFlight)
  ccb.current = onCheckFlight
  const ecb = useRef(onEvent)
  ecb.current = onEvent
  const ffcb = useRef(onFreeFlight)
  ffcb.current = onFreeFlight

  useEffect(() => {
    let ws: WebSocket | null = null
    let closed = false
    let retry: ReturnType<typeof setTimeout>

    const connect = () => {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      ws = new WebSocket(`${proto}://${location.host}/ws/telemetry`)
      ws.onopen = () => setWsOpen(true)
      ws.onmessage = e => {
        const m = JSON.parse(e.data) as WsEvent
        if (m.type === 'telemetry') { setTele(m); setLink(m.connection) }
        else if (m.type === 'state') {
          setLink(m.connection)
          if (m.connection !== 'Connected') setTele(null) // sim gone → reset the gauges to —
        }
        else if (m.type === 'settled') cb.current(m)
        else if (m.type === 'diverted') dcb.current(m)
        else if (m.type === 'checkflight') ccb.current(m)
        else if (m.type === 'event') ecb.current?.(m)
        else if (m.type === 'freeflight') ffcb.current?.(m as { flightId: string; touchdownFpm: number; overallScore: number | null } & { type: string })
      }
      ws.onclose = () => { setWsOpen(false); if (!closed) retry = setTimeout(connect, 1500) }
      ws.onerror = () => ws?.close()
    }
    connect()
    return () => { closed = true; clearTimeout(retry); ws?.close() }
  }, [])

  return { tele, wsOpen, link }
}

/** Map the server link state to an honest HUD badge — never a green "connected" without frames. */
function linkBadge(wsOpen: boolean, link: string): { text: string; tone: string } {
  if (!wsOpen) return { text: 'reconnecting…', tone: 'down' }
  switch (link) {
    case 'Connected': return { text: 'live', tone: 'up' }
    case 'Connecting': return { text: 'connecting…', tone: 'warn' }
    case 'SimExited': return { text: 'sim closed', tone: 'down' }
    default: return { text: 'waiting for sim', tone: 'warn' } // Disconnected
  }
}

// Bearing in degrees (0 = north) from one lat/lon to the next — points the aircraft marker along its track.
function bearing(a: [number, number], b: [number, number]): number {
  const r = Math.PI / 180
  const dLon = (b[1] - a[1]) * r
  const y = Math.sin(dLon) * Math.cos(b[0] * r)
  const x = Math.cos(a[0] * r) * Math.sin(b[0] * r) - Math.sin(a[0] * r) * Math.cos(b[0] * r) * Math.cos(dLon)
  return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360
}

// Great-circle distance in nm (matches the server's GeoMath) — for the ferry fee estimate.
function distNm(a: [number, number], b: [number, number]): number {
  const R = 3440.065, toR = (d: number) => (d * Math.PI) / 180
  const dLat = toR(b[0] - a[0]), dLon = toR(b[1] - a[1])
  const s = Math.sin(dLat / 2) ** 2 + Math.cos(toR(a[0])) * Math.cos(toR(b[0])) * Math.sin(dLon / 2) ** 2
  return 2 * R * Math.asin(Math.min(1, Math.sqrt(s)))
}

// Does the live sim aircraft (its MSFS title) plausibly match the career tail the player picked? NeoFly's
// "matching plane" check. Fuzzy on purpose: MSFS titles carry livery/variant suffixes ("… SF", "… G1000"),
// so we match when the tail's model/type tokens are present in the sim title rather than demanding equality.
function matchesSimTitle(simTitle: string | undefined, ac: OwnedAircraft | null): boolean {
  if (!simTitle || !ac) return false
  const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim()
  const sim = norm(simTitle)
  if (!sim) return false
  const model = norm(ac.icaoModel || '')
  if (model && sim.includes(model)) return true
  // Otherwise: most of the aircraft-name's significant tokens should appear in the sim title.
  const stop = new Set(['the', 'and', 'of', 'aircraft', 'plane'])
  const tokens = norm(ac.name).split(' ').filter(t => t.length > 2 && !stop.has(t))
  if (tokens.length === 0) return false
  const hit = tokens.filter(t => sim.includes(t)).length
  return hit / tokens.length >= 0.6
}

// A deterministic passenger manifest for a cabin — the same assignment always yields the same souls aboard
// (NeoFly's cabin list). Pure client-side flavour: no money, no server, seeded off the assignment id so it's
// stable across reloads. Names are a fixed, deliberately innocuous international pool.
const PAX_FIRST = ['Amir', 'Sofia', 'Liam', 'Noah', 'Emma', 'Yuki', 'Chen', 'Priya', 'Omar', 'Lucas', 'Mia', 'Aisha', 'Diego', 'Elena', 'Kwame', 'Ingrid', 'Hana', 'Tariq', 'Nadia', 'Marco', 'Freya', 'Ravi', 'Zara', 'Kenji', 'Lucia', 'Sven', 'Amara', 'Tom', 'Ana', 'Leo']
const PAX_LAST = ['Nguyen', 'Okafor', 'Kowalski', 'Rossi', 'Haddad', 'Andersson', 'Yamamoto', 'Silva', 'Patel', 'Kim', 'Muller', 'Costa', 'Ivanov', 'Dubois', 'Larsen', 'Tanaka', 'Reyes', 'Novak', 'Hassan', 'Bauer', 'Sato', 'Moreau', 'Petrov', 'Singh', 'Weber', 'Lindqvist', 'Mensah', 'Fischer', 'Adams', 'Romano']
interface Passenger { seat: string; name: string; age: number }
function cabinManifest(seed: string, pax: number): Passenger[] {
  // xmur3 hash → mulberry32 PRNG: tiny, deterministic, no deps.
  let h = 1779033703 ^ seed.length
  for (let i = 0; i < seed.length; i++) { h = Math.imul(h ^ seed.charCodeAt(i), 3432918353); h = (h << 13) | (h >>> 19) }
  let a = (h ^= h >>> 16) >>> 0
  const rnd = () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296 }
  const rows = 'ABCDEF'
  const out: Passenger[] = []
  for (let i = 0; i < pax; i++) {
    const seat = `${Math.floor(i / 2) + 1}${rows[i % 2]}`
    out.push({ seat, name: `${PAX_FIRST[Math.floor(rnd() * PAX_FIRST.length)]} ${PAX_LAST[Math.floor(rnd() * PAX_LAST.length)]}`, age: 18 + Math.floor(rnd() * 55) })
  }
  return out
}

function planeIcon(hdg: number): L.DivIcon {
  return L.divIcon({
    className: 'plane-marker',
    html: `<svg viewBox="0 0 24 24" style="transform:rotate(${hdg}deg)"><path d="M12 2c.7 0 1.2 1.1 1.2 2.6v5.1l8 4.6v1.9l-8-2.7v4.4l2.3 1.7v1.4L12 20l-3.5 1.3v-1.4l2.3-1.7v-4.4l-8 2.7v-1.9l8-4.6V4.6C10.8 3.1 11.3 2 12 2z"/></svg>`,
    iconSize: [34, 34], iconAnchor: [17, 17],
  })
}

// What THIS leg is graded on (NeoFly's flight objectives) — the scoring rubric made visible so the player
// knows the targets before they fly. Derived from the mission type; the actual grading happens at settlement.
function missionObjectives(type: string, hasDeadline: boolean): { label: string; hint: string }[] {
  const t = type.toLowerCase()
  const extra: { label: string; hint: string }[] = []
  if (['vip', 'tourist', 'passenger'].includes(t)) extra.push({ label: 'Comfortable ride', hint: 'Gentle g and bank — a smooth ride tips (and keeps the client)' })
  if (['sensitive', 'hazardous'].includes(t)) extra.push({ label: 'Gentle touchdown', hint: 'Fragile load — a firm arrival damages it, a slam destroys it' })
  if (hasDeadline) extra.push({ label: 'On time', hint: 'Land before the delivery deadline or the fee is docked' })
  return [
    ...extra,
    { label: 'Smooth landing', hint: 'Touch down under ~200 fpm, wings level' },
    { label: 'Stabilised approach', hint: 'On speed and on glidepath below the gate' },
    { label: 'Clean flight', hint: 'No overspeed, over-bank, or stalls enroute' },
  ]
}

function ObjectivesCard({ leg }: { leg: Assignment }) {
  const objectives = missionObjectives(leg.type, !!leg.deadlineAt)
  return (
    <section className="card">
      <div className="row-head"><h2>Objectives</h2><span className="hint">{spaced(leg.type)} · graded on landing</span></div>
      <ul className="obj-list">
        {objectives.map(o => (
          <li key={o.label} className="obj">
            <svg viewBox="0 0 24 24" className="obj-tick" aria-hidden="true"><path d="M20 6L9 17l-5-5" /></svg>
            <div><b>{o.label}</b><div className="obj-hint muted">{o.hint}</div></div>
          </li>
        ))}
      </ul>
    </section>
  )
}

// The cabin manifest for a passenger leg (NeoFly's Cabin list) — who's aboard, their seat and age. Purely
// cosmetic flavour, generated deterministically from the assignment so it's stable for the whole flight.
function CabinCard({ leg }: { leg: Assignment }) {
  const pax = useMemo(() => cabinManifest(leg.id, leg.pax), [leg.id, leg.pax])
  return (
    <section className="card">
      <div className="row-head"><h2>Cabin</h2><span className="hint">{leg.pax} aboard · {leg.origin} → {leg.dest}</span></div>
      <div className="tbl-wrap">
        <table className="tbl cabin-tbl">
          <thead><tr><th>Seat</th><th>Passenger</th><th>Age</th></tr></thead>
          <tbody>
            {pax.map(p => <tr key={p.seat}><td className="num">{p.seat}</td><td>{p.name}</td><td className="num">{p.age}</td></tr>)}
          </tbody>
        </table>
      </div>
    </section>
  )
}

// The live moving-map on the Flight screen. Built ONCE; each telemetry frame just moves the aircraft
// marker and extends the trail (never rebuilds the map, so tracking stays smooth). Esri satellite tiles.
//
// Until a leg is ARMED (begun), the map shows the selected aircraft PARKED AT ITS OWN FIELD (`home`) and
// ignores telemetry entirely — so an always-streaming synthetic source, or a real sim sitting at some
// unrelated spot, never paints a phantom "flight" before you've actually started one (the NeoFly rule:
// your aircraft lives where it is; the moving map only comes alive once you fly the leg).
function FlightMap({ tele, leg, home, live }: { tele: Telemetry | null; leg?: Assignment | null; home?: { lat: number; lon: number; label: string } | null; live?: boolean }) {
  const host = useRef<HTMLDivElement>(null)
  const mapRef = useRef<L.Map | null>(null)
  const marker = useRef<L.Marker | null>(null)
  const parked = useRef<L.Marker | null>(null)
  const trail = useRef<L.Polyline | null>(null)
  const legLayer = useRef<L.LayerGroup | null>(null)
  const path = useRef<[number, number][][]>([[]]) // trail as SEGMENTS — a pause/gap starts a new one (no false chord)
  const lastPos = useRef<[number, number] | null>(null)
  const heading = useRef(0) // last committed heading — only re-aimed on real travel, so jitter can't spin the icon
  const centred = useRef(false)
  const online = typeof navigator === 'undefined' ? true : navigator.onLine
  const armed = !!leg || !!live

  useEffect(() => {
    if (!host.current || !online) return
    const map = L.map(host.current, { attributionControl: true, zoomControl: false, worldCopyJump: true }).setView([25, 0], 2)
    L.control.zoom({ position: 'topright' }).addTo(map) // top-left is where the HUD overlay lives
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
      attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics', maxZoom: 18,
    }).addTo(map)
    legLayer.current = L.layerGroup().addTo(map)
    trail.current = L.polyline([], { color: '#6d84ff', weight: 3, opacity: .85 }).addTo(map)
    mapRef.current = map
    const t = setTimeout(() => map.invalidateSize(), 60) // WebView2 flex layout can settle a beat late
    return () => { clearTimeout(t); map.remove(); mapRef.current = null; marker.current = null; parked.current = null; trail.current = null; legLayer.current = null; path.current = [[]]; lastPos.current = null; centred.current = false }
  }, [online])

  // The planned leg — departure + destination pins, a dashed planned track, and the 5 nm arrival ring.
  useEffect(() => {
    const map = mapRef.current, layer = legLayer.current
    if (!map || !layer) return
    layer.clearLayers()
    const hasDest = leg && (leg.destLat !== 0 || leg.destLon !== 0)
    if (!leg || !hasDest) return
    const d: [number, number] = [leg.destLat, leg.destLon]
    const hasOrigin = leg.originLat !== 0 || leg.originLon !== 0
    if (hasOrigin) {
      const o: [number, number] = [leg.originLat, leg.originLon]
      L.polyline([o, d], { color: '#6d84ff', weight: 2, opacity: .7, dashArray: '6 8' }).addTo(layer)
      L.circleMarker(o, { radius: 5, color: '#fff', weight: 2, fillColor: '#8a97a7', fillOpacity: .9 }).addTo(layer).bindTooltip(leg.origin, { direction: 'top', className: 'sat-tip' })
    }
    L.circle(d, { radius: 5 * 1852, color: '#6d84ff', weight: 1, opacity: .45, fill: false, dashArray: '3 6' }).addTo(layer) // 5 nm settle radius
    L.circleMarker(d, { radius: 6, color: '#6d84ff', weight: 2, fillColor: '#6d84ff', fillOpacity: .9 }).addTo(layer).bindTooltip(leg.dest, { permanent: true, direction: 'top', className: 'sat-tip' })
    if (!centred.current) {
      if (hasOrigin) map.fitBounds(L.latLngBounds([leg.originLat, leg.originLon], d).pad(0.4), { maxZoom: 9 })
      else map.setView(d, 8)
    }
  }, [leg])

  // Idle: park the selected aircraft at its own field and ignore telemetry. Once a leg is armed, this
  // marker is torn down so the live plot below can take over.
  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    if (armed || !home || (home.lat === 0 && home.lon === 0)) {
      if (parked.current) { parked.current.remove(); parked.current = null }
      return
    }
    // Disarmed → drop any live plot + trail so nothing lingers from a prior flight.
    if (marker.current) { marker.current.remove(); marker.current = null }
    path.current = [[]]; lastPos.current = null; heading.current = 0; trail.current?.setLatLngs([]); centred.current = false
    const pos: [number, number] = [home.lat, home.lon]
    if (!parked.current) parked.current = L.marker(pos, { icon: planeIcon(0), interactive: false }).addTo(map).bindTooltip(`${home.label} · parked at ${home.label}`, { direction: 'top', className: 'sat-tip' })
    else parked.current.setLatLng(pos)
    parked.current.bindTooltip(`parked at ${home.label}`, { direction: 'top', className: 'sat-tip' })
    map.setView(pos, 9)
  }, [home, armed])

  useEffect(() => {
    const map = mapRef.current
    if (!map || !armed || !tele || (tele.lat === 0 && tele.lon === 0)) return
    const pos: [number, number] = [tele.lat, tele.lon]
    const prev = lastPos.current
    const step = prev ? Math.hypot(pos[0] - prev[0], pos[1] - prev[1]) : Infinity
    // Only extend the trail and re-aim the icon when the aircraft has actually TRAVELLED. Below ~15 m the
    // reading is GPS jitter (or a not-yet-settled sim): computing a bearing off it spins the icon and litters
    // the trail, so we glide the marker but hold heading. A big jump (~9 nm+) is a pause/teleport/telemetry
    // gap, not flight — start a NEW trail segment so we don't draw a false straight line across the hole.
    const MIN_MOVE_DEG = 0.00015, JUMP_DEG = 0.15
    if (step > MIN_MOVE_DEG) {
      if (prev && step <= JUMP_DEG) heading.current = bearing(prev, pos)
      if (prev && step > JUMP_DEG) path.current.push([]) // gap → break the line
      path.current[path.current.length - 1].push(pos)
      let total = path.current.reduce((n, s) => n + s.length, 0)
      while (total > 500 && path.current.length > 0) { path.current[0].shift(); if (path.current[0].length === 0 && path.current.length > 1) path.current.shift(); total-- }
      lastPos.current = pos
      trail.current?.setLatLngs(path.current)
    }
    if (!marker.current) marker.current = L.marker(pos, { icon: planeIcon(heading.current), interactive: false }).addTo(map)
    else { marker.current.setLatLng(pos); marker.current.setIcon(planeIcon(heading.current)) }
    if (!centred.current) { map.setView(pos, 8); centred.current = true }
    else map.panTo(pos, { animate: true, duration: .5 })
  }, [tele, armed])

  if (!online) return <div className="flightmap-empty">The moving-map needs a connection for satellite imagery.</div>
  return <div className="satmap flightmap" ref={host} role="img" aria-label="Live flight map" />
}

type LogSev = 'info' | 'ok' | 'warn' | 'bad' | 'coach'

// Map a server FlightEventSeverity to a log tone (Phase 7a). Coaching (Phase 9, the Fun Dial) gets its own
// calm tone — a friendly nudge, deliberately not the alarming warn colour.
function evSev(severity: string): LogSev {
  return severity === 'Success' ? 'ok' : severity === 'Warning' ? 'warn' : severity === 'Coaching' ? 'coach' : 'info'
}

// A 0..100 flight score to a colour tone (Phase 7b).
function scoreTone(s: number | null): string {
  if (s == null) return 'muted'
  return s >= 75 ? 'pos' : s >= 45 ? '' : 'neg'
}

// A frozen delivery deadline to a short "due in …" label (Phase 7d). Recomputed each render/reload.
function dueText(iso: string): string {
  const ms = Date.parse(iso) - Date.now()
  if (ms <= 0) return 'overdue'
  const min = Math.round(ms / 60000)
  return min >= 60 ? `due in ${Math.floor(min / 60)}h ${min % 60}m` : `due in ${min}m`
}
interface LogEntry { id: number; at: string; sev: LogSev; text: string }
const clock = () => new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })

// The live event-log / narration channel — the running story of the flight (connection, phases, settlement,
// warnings), timestamped and severity-coded, the way a real ops console talks back to you.
function FlightLog({ log }: { log: LogEntry[] }) {
  const end = useRef<HTMLDivElement>(null)
  useEffect(() => { end.current?.scrollIntoView({ block: 'nearest' }) }, [log])
  return (
    <div className="flog" aria-live="polite">
      <div className="flog-head"><span className="flog-dotlive" /> Flight log</div>
      <div className="flog-body">
        {log.map(e => (
          <div key={e.id} className={`flog-row ${e.sev}`}>
            <span className="flog-time num">{e.at}</span>
            <span className="flog-mark" />
            <span className="flog-text">{e.text}</span>
          </div>
        ))}
        <div ref={end} />
      </div>
    </div>
  )
}

function Flight({ state, onSettled }: { state: State; onSettled: () => void }) {
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [begun, setBegun] = useState<Assignment | null>(null)
  const [freeFlight, setFreeFlight] = useState(false) // Phase 12 — flying with no job, tracked + logged
  const [settled, setSettled] = useState<Settled | null>(null)
  const [diverted, setDiverted] = useState<Diverted | null>(null)
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [aircraftId, setAircraftId] = useState('')
  const [crew, setCrew] = useState<Staff[]>([])   // Phase 13 — hire-out: fly a job as yourself or hand it to a crew
  const [who, setWho] = useState('')              // '' = you (hand-fly); else a staffId (they fly it autonomously)
  const [beginErr, setBeginErr] = useState<string | null>(null)
  const [quals, setQuals] = useState<QualClass[]>([])
  const [checkPending, setCheckPending] = useState<string | null>(null) // class name of a check-flight in progress
  const [checkResult, setCheckResult] = useState<CheckFlightDone | null>(null)
  const [centresFor, setCentresFor] = useState<string | null>(null) // class whose test centres are expanded
  const [centres, setCentres] = useState<CheckCentre[]>([])
  const [log, setLog] = useState<LogEntry[]>(() => [{ id: 0, at: clock(), sev: 'info', text: 'Flight console ready — standing by.' }])
  const logId = useRef(1)
  const addLog = useCallback((sev: LogSev, text: string) => setLog(l => [...l, { id: logId.current++, at: clock(), sev, text }].slice(-80)), [])

  const loadAssignments = useCallback(() => { api.assignments().then(setAssignments).catch(() => {}) }, [])
  const loadCrew = useCallback(() => { api.staff().then(s => setCrew(s.filter(x => x.role !== 'Manager'))).catch(() => {}) }, [])
  const loadQuals = useCallback(() => { api.quals().then(setQuals).catch(() => {}) }, [])
  const loadFleet = useCallback(() => {
    api.hangar().then(hs => {
      const avail = hs.filter(h => h.availability === 'Available')
      setFleet(avail)
      // Default to an aircraft that's rated AND parked here with you (the only kind you can actually fly a job
      // in), else a rated one, else the first available.
      setAircraftId(prev => prev
        || avail.find(h => h.rated && h.locationIcao === state.currentIcao)?.id
        || avail.find(h => h.rated)?.id || avail[0]?.id || '')
    }).catch(() => {})
  }, [state.currentIcao])
  useEffect(() => { loadAssignments(); loadFleet(); loadQuals(); loadCrew() }, [loadAssignments, loadFleet, loadQuals, loadCrew])

  const { tele, wsOpen, link } = useTelemetry(
    s => {
      setSettled(s)
      setDiverted(null)
      setBegun(null)
      onSettled()
      loadAssignments()
      loadFleet() // the airframe moved to the destination + ticked hours
      addLog('ok', `Job settled — ${money(s.payoutCents)}, +${s.xp} XP${s.payloadMatched ? ' (aircraft bonus)' : ''}.`)
      if (s.promotedTo) addLog('ok', `Promoted to ${s.promotedTo}.`)
    },
    d => { setDiverted(d); addLog('warn', `Landed ${Math.round(d.distanceNm)} nm off ${d.destIcao} — the job stays open, fly on.`) },
    c => { // a check-flight was graded on landing (3d)
      setCheckResult(c)
      setCheckPending(null)
      onSettled()      // cash changed (the fee)
      loadQuals()      // a pass adds/upgrades a class
      loadFleet()      // newly-rated aircraft become flyable
      addLog(c.passed ? 'ok' : 'bad', `Check-flight ${c.className}: ${c.passed ? 'passed' : 'failed'} at ${signed(Math.round(c.touchdownFpm))} fpm.`)
    },
    e => addLog(evSev(e.severity), e.message), // the real scored moments the tracker emits (Phase 7a)
    f => { // a free flight finished — logged, no payout (Phase 12)
      setFreeFlight(false)
      onSettled() // refresh flights/highlights
      addLog('ok', `Free flight logged${f.overallScore != null ? ` — score ${f.overallScore}/100` : ''}.`)
    },
  )
  const badge = linkBadge(wsOpen, link)

  // Narrate connection + phase transitions into the flight log (only on an actual change).
  const prevOpen = useRef(wsOpen), prevLink = useRef(link), prevPhase = useRef<string | null>(null)
  const loggedPhases = useRef<Set<string>>(new Set())
  useEffect(() => {
    if (prevOpen.current !== wsOpen) { addLog(wsOpen ? 'ok' : 'warn', wsOpen ? 'Connected to Callsign.' : 'Connection lost — reconnecting…'); prevOpen.current = wsOpen }
  }, [wsOpen, addLog])
  useEffect(() => {
    if (prevLink.current === link) return
    // The sim reconnect loop flips Connecting↔Disconnected every few seconds while MSFS is closed.
    // Retry silently — only narrate real edges: a live connection, losing a live link, or the sim exiting.
    const wasConnected = prevLink.current === 'Connected'
    if (link === 'Connected') addLog('ok', 'Simulator connected — telemetry is live.')
    else if (link === 'SimExited') addLog('bad', 'Simulator closed.')
    else if (wasConnected) addLog('warn', 'Lost the simulator — waiting to reconnect…')
    // else: Connecting/Disconnected churn before ever connecting → say nothing.
    prevLink.current = link
  }, [link, addLog])
  useEffect(() => {
    // Only narrate rare, meaningful beats — takeoff and touchdown already arrive as scored events from
    // the server, so we drop the climb/cruise/descent chatter and log each cue at most once per flight.
    const PHASE_LOG: Record<string, string> = { Approach: 'On approach.', Shutdown: 'Aircraft secured.' }
    const p = tele?.phase ?? null
    if (!p) return
    if (p === 'Parked' || p === 'Taxi') loggedPhases.current.clear() // new flight cycle on the ground
    const label = PHASE_LOG[p]
    if (label && !loggedPhases.current.has(p)) { addLog('info', label); loggedPhases.current.add(p) }
    prevPhase.current = p
  }, [tele?.phase, addLog])

  const beginCheck = async (cls: string, name: string) => {
    setBeginErr(null); setCheckResult(null)
    try { await api.beginCheckFlight(cls); setCheckPending(name); setCentresFor(null) }
    catch (e) { setBeginErr(cleanErr(e)) }
  }
  const showCentres = async (cls: string) => {
    if (centresFor === cls) { setCentresFor(null); return }
    setCentresFor(cls)
    try { setCentres(await api.checkCentres(cls)) } catch { setCentres([]) }
  }

  const begin = async (a: Assignment) => {
    setSettled(null)
    setDiverted(null)
    setBeginErr(null)
    setFreeFlight(false)
    try {
      await api.beginFlight(a.id, aircraftId || undefined)
      setBegun(a)
      addLog('info', `Leg begun — ${a.origin} → ${a.dest} (${a.destName}). Fly it and land at ${a.dest}.`)
    } catch (e) {
      setBeginErr(cleanErr(e)) // e.g. "You're not rated for the …"
    }
  }
  const cancelJob = async (a: Assignment) => {
    if (!confirm(`Cancel the job to ${a.dest}? You'll hand it back — the client's opinion of you dips a little.`)) return
    setBeginErr(null)
    try {
      const r = await api.cancelAssignment(a.id)
      if (begun?.id === a.id) setBegun(null)
      loadAssignments(); onSettled()
      addLog('warn', `Cancelled ${a.origin} → ${a.dest}${r.clientName ? ` — ${r.clientName} noted it` : ''}.`)
    } catch (e) { setBeginErr(cleanErr(e)) }
  }
  const travelTo = async (icao: string) => {
    setBeginErr(null)
    try { const r = await api.relocateSelf(icao); onSettled(); loadAssignments(); addLog('ok', `Travelled to ${icao} — ${money(r.feeCents)}. You can fly from here now.`) }
    catch (e) { setBeginErr(cleanErr(e)) }
  }
  const handOff = async (a: Assignment) => {
    setBeginErr(null)
    const crewName = crew.find(c => c.id === who)?.name ?? 'the crew'
    try {
      await api.handOffAssignment(a.id, who, aircraftId)
      loadAssignments(); loadFleet(); onSettled()
      addLog('ok', `Handed ${a.origin} → ${a.dest} to ${crewName} — they'll fly it and it banks automatically.`)
    } catch (e) { setBeginErr(cleanErr(e)) }
  }
  // Auto-arm on takeoff (Phase 13): if you start rolling with exactly one flyable job and the right aircraft
  // loaded, arm it for you — so the flight "just starts when you go" instead of needing a button press. Tightly
  // guarded (connected, on ground at the origin, the matching aircraft, a real takeoff-roll speed) and fires once.
  const autoArmed = useRef(false)
  useEffect(() => { if (!begun) autoArmed.current = false }, [begun])
  useEffect(() => {
    if (begun || freeFlight || who || autoArmed.current) return // 'who' set = you're handing it to a crew, not flying
    if (link !== 'Connected' || !tele || tele.onGround !== true || tele.gs < 35) return
    const flyable = assignments.filter(a =>
      state.currentIcao === a.origin && selAc?.locationIcao === a.origin && !!aircraftId
      && payloadFit(selAc, a.pax, a.weightLbs).ok && matchesSimTitle(tele.title, selAc))
    if (flyable.length === 1) { autoArmed.current = true; void begin(flyable[0]) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tele, link, begun, freeFlight, who, assignments, aircraftId, selAc, state.currentIcao])
  const startFreeFlight = async () => {
    setSettled(null); setDiverted(null); setBeginErr(null); setBegun(null)
    try { await api.beginFreeFlight(); setFreeFlight(true); addLog('info', 'Free flight armed — fly anywhere; it\'s tracked and logged, no job attached.') }
    catch (e) { setBeginErr(cleanErr(e)) }
  }

  // The aircraft picked to fly, and where it's parked — drives the idle map (parked at its field) and the
  // readiness check below.
  const selAc = fleet.find(f => f.id === aircraftId) ?? null
  const home = selAc && (selAc.lat !== 0 || selAc.lon !== 0) ? { lat: selAc.lat, lon: selAc.lon, label: selAc.locationIcao } : null
  // Can the chosen aircraft physically carry the job? Seats for the pax, useful load for the cargo (NeoFly's
  // "missing lbs" check). Unknown specs (null) don't block.
  const payloadFit = (ac: OwnedAircraft | null, pax: number, weightLbs: number): { ok: boolean; msg?: string } => {
    if (!ac) return { ok: true }
    // A passenger job is gated on SEATS; a cargo job on USEFUL LOAD — the same split the settlement bonus uses.
    // (Gating a pax job on useful load too was double-jeopardy: a 4-seat plane failed a 4-pax job on 2 lb.)
    if (pax > 0) {
      if (ac.seats != null && ac.seats < pax) return { ok: false, msg: `${ac.tail} seats ${ac.seats} — this job needs ${pax}` }
      return { ok: true }
    }
    if (weightLbs > 0 && ac.usefulLoadLbs != null && ac.usefulLoadLbs < weightLbs)
      return { ok: false, msg: `${ac.tail} carries ${ac.usefulLoadLbs.toLocaleString()} lb — needs ${weightLbs.toLocaleString()} (missing ${(weightLbs - ac.usefulLoadLbs).toLocaleString()} lb)` }
    return { ok: true }
  }

  return (
    <div className="grid">
      <section className="card hud">
        <div className="hud-head">
          <h2>Live flight</h2>
          <span className={`conn ${badge.tone}`}>{badge.text}</span>
        </div>
        <div className="hud-live">
          <div className="flightmap-wrap">
            <FlightMap tele={tele} leg={begun} home={home} live={freeFlight} />
            <div className="fm-overlay">
              <span className="fm-phase num">{(begun || freeFlight) ? (tele?.phase ?? 'STANDING BY') : (home ? 'PARKED' : 'STANDING BY')}</span>
              <span className="fm-reads">
                <span className="fm-read"><b className="num">{tele ? Math.round(tele.alt).toLocaleString() : '—'}</b> ft</span>
                <span className="fm-read"><b className="num">{tele ? Math.round(tele.ias) : '—'}</b> kt</span>
                <span className={`fm-read ${tele ? (tele.vs < -50 ? 'down' : tele.vs > 50 ? 'up' : '') : ''}`}><b className="num">{tele ? signed(Math.round(tele.vs)) : '—'}</b> fpm</span>
                <span className="fm-read"><b>{tele ? (tele.onGround ? 'GND' : 'AIR') : '—'}</b></span>
              </span>
            </div>
            {!begun && !home && <div className="flightmap-veil">Select an available aircraft — it'll show parked at its field. Begin a leg to fly.</div>}
          </div>
          <FlightLog log={log} />
        </div>
        {diverted && <div className="banner warn">You landed {Math.round(diverted.distanceNm)} nm from <b>{diverted.destIcao}</b>. The job's still open — take off and fly on to {diverted.destIcao}.</div>}
        {begun
          ? <>
            <div className="banner ok">Armed <b>{begun.origin} → {begun.dest}</b> · {begun.destName} — take off to start; land at {begun.dest}, then park to settle.</div>
            {tele?.onGround && (tele.phase === 'Landing' || tele.phase === 'Shutdown') && (() => {
              const landed = true // on the ground after flying
              const braked = !!tele.parkingBrake
              const engOff = tele.engineRunning === false
              const Step = ({ ok, label }: { ok: boolean; label: string }) =>
                <span className={`fin-step ${ok ? 'ok' : 'no'}`}><span className="r-dot" />{label}</span>
              return (
                <div className="finish-checklist" title="Land at the destination, set the parking brake, and shut the engine down — then the job settles.">
                  <span className="r-title">To finish</span>
                  <Step ok={landed} label={`Landed at ${begun.dest}`} />
                  <Step ok={braked} label="Parking brake set" />
                  <Step ok={engOff} label="Engine shut down" />
                </div>
              )
            })()}
          </>
          : freeFlight
            ? <div className="banner ok">Free flight — fly anywhere. Land and it's logged (scored, no payout, no job).</div>
            : <div className="hint">Begin a leg below, or fly free. The next landing at the destination settles the job.</div>}
        {!begun && !freeFlight && (() => {
          const simOn = link === 'Connected'
          const acHere = !!selAc && selAc.locationIcao === state.currentIcao
          const acMatch = simOn && matchesSimTitle(tele?.title, selAc)
          const Row = ({ ok, wait, label }: { ok: boolean; wait?: boolean; label: string }) =>
            <span className={`r-item ${ok ? 'ok' : wait ? 'wait' : 'no'}`}><span className="r-dot" />{label}</span>
          return (
            <div className="readiness" title="NeoFly-style check: the leg comes alive when your aircraft and you are at the field and the sim is flying the right plane.">
              <span className="r-title">Matching plane &amp; location</span>
              <Row ok={simOn} wait={!simOn} label={simOn ? 'Simulator connected' : 'Waiting for simulator'} />
              <Row ok={acHere} label={acHere ? `${selAc?.tail} with you at ${state.currentIcao}` : selAc ? `${selAc.tail} is at ${selAc.locationIcao}, not ${state.currentIcao}` : 'No aircraft selected'} />
              <Row ok={acMatch} wait={!simOn} label={!simOn ? 'Sim aircraft — start MSFS' : acMatch ? 'Sim aircraft matches' : `Load your ${selAc?.name ?? 'aircraft'} in MSFS${tele?.title ? ` — the sim has “${tele.title}” loaded` : ''}`} />
            </div>
          )
        })()}
      </section>

      {settled && <SettlementCard settled={settled} />}
      {checkPending && <div className="banner ok">Check-flight for <b>{checkPending}</b> in progress — fly a clean landing (≤ 200 fpm) and it grades automatically.</div>}
      {checkResult && <CheckFlightCard result={checkResult} />}
      {begun && <ObjectivesCard leg={begun} />}
      {begun && begun.pax > 0 && <CabinCard leg={begun} />}

      <section className="card">
        <div className="row-head">
          <h2>Ready to fly</h2>
          {fleet.length > 0
            ? <label className="pick">Aircraft&nbsp;
                <select value={aircraftId} onChange={e => setAircraftId(e.target.value)}>
                  {fleet.map(f => {
                    const here = f.locationIcao === state.currentIcao
                    const note = !f.rated ? ' · not rated' : !here ? ` · at ${f.locationIcao}, not here` : ''
                    return <option key={f.id} value={f.id} disabled={!f.rated || !here}>{f.tail} · {f.name} — {f.locationIcao}{note}</option>
                  })}
                </select>
              </label>
            : <span className="hint">No available aircraft — buy one in the Hangar.</span>}
          <button className="ghost small" disabled={!!begun || freeFlight} title="Fly with no job — tracked and logged, no payout" onClick={startFreeFlight}>
            {freeFlight ? 'Free flight in progress…' : 'Free flight'}
          </button>
        </div>
        {beginErr && <div className="banner error" onClick={() => setBeginErr(null)}>{beginErr} — tap to dismiss</div>}
        {crew.length > 0 && (
          <div className="fly-as">
            <span className="metalabel">Flying as</span>
            <select value={who} onChange={e => setWho(e.target.value)} disabled={!!begun || freeFlight}>
              <option value="">You — hand-fly &amp; scored</option>
              {crew.map(c => <option key={c.id} value={c.id}>{c.name} · {Math.round(c.skillMilli / 1000)}%</option>)}
            </select>
            {who && <span className="muted">they'll fly it autonomously — it banks on completion (no telemetry score)</span>}
          </div>
        )}
        {assignments.length === 0
          ? <div className="empty"><p>No accepted jobs. Accept one on the Jobs board first.</p></div>
          : (
            <ul className="assign-list">
              {assignments.map(a => {
                const isCrew = !!who
                const crewName = crew.find(c => c.id === who)?.name ?? 'the crew'
                const atOrigin = state.currentIcao === a.origin           // you're at the departure field
                const acHere = !!selAc && selAc.locationIcao === a.origin  // the chosen aircraft is too
                const pf = payloadFit(selAc, a.pax, a.weightLbs)          // it can carry the load
                const canFly = atOrigin && acHere && !!aircraftId && pf.ok
                const canSend = acHere && !!aircraftId && pf.ok            // crew flies it — YOUR location doesn't matter
                const why = begun?.id === a.id ? null
                  : !aircraftId ? 'Pick an aircraft'
                  : !acHere ? `${selAc?.tail ?? 'That aircraft'} isn't at ${a.origin} — ferry it there first`
                  : !pf.ok ? pf.msg
                  : (!isCrew && !atOrigin) ? `You're at ${state.currentIcao} — this leg departs ${a.origin}`
                  : null
                return (
                  <li key={a.id} className="assign">
                    <div className="leg"><b>{a.origin}</b> → <b>{a.dest}</b> <span className="muted">{a.destName} · {a.commodity}</span></div>
                    <div className="assign-meta">
                      <span>{Math.round(a.distanceNm)} nm</span>
                      <span>{loadText(a.type, a.weightLbs, a.pax)}</span>
                      <span className="pos">{money(a.rewardQuoteCents)}</span>
                      {a.deadlineAt && <span className={`due ${Date.parse(a.deadlineAt) < Date.now() ? 'neg' : 'warn'}`}>{dueText(a.deadlineAt)}</span>}
                    </div>
                    <div className="assign-actions">
                      {isCrew
                        ? <button className="primary" disabled={!!begun || !canSend} onClick={() => handOff(a)} title={why ?? `${crewName} flies it autonomously`}>Send {crewName}</button>
                        : <button className="primary" disabled={begun?.id === a.id || !canFly} onClick={() => begin(a)} title={why ?? 'Arms the leg — it starts scoring the moment you take off'}>
                            {begun?.id === a.id ? 'In progress…' : 'Fly this job'}
                          </button>}
                      {!isCrew && !atOrigin && begun?.id !== a.id && <button className="ghost small" disabled={!!begun} onClick={() => travelTo(a.origin)} title={`Fly commercial to ${a.origin} so you can depart from there`}>Travel to {a.origin}</button>}
                      <button className="linky" disabled={begun?.id === a.id} onClick={() => cancelJob(a)} title="Hand the job back — a small hit to the client's loyalty">Cancel</button>
                    </div>
                    {why && begun?.id !== a.id && <div className="assign-why muted">{why}</div>}
                  </li>
                )
              })}
            </ul>
          )}
        <p className="hint muted">Signed in as {state.name} · flying out of {state.currentIcao}.</p>
      </section>

      <section className="card">
        <div className="row-head"><h2>Earn a rating</h2><span className="hint">Fly a clean landing (≤ 200 fpm) to pass</span></div>
        {quals.filter(q => !q.held || q.stars < 5).length === 0
          ? <div className="empty">You hold every rating at full marks.</div>
          : (
            <ul className="assign-list">
              {quals.filter(q => !q.held || q.stars < 5).map(q => (
                <li key={q.class} className="assign">
                  <div className="leg">{q.displayName}{q.held && <span className="muted"> · held {q.stars}★</span>}</div>
                  <div className="assign-meta">
                    <span className="muted">{q.description}</span>
                    <span className="num">{money(q.checkFlightFeeCents)}</span>
                  </div>
                  <div className="qual-actions">
                    <button className="ghost small" onClick={() => showCentres(q.class)}>{centresFor === q.class ? 'Hide centres' : 'Test centres'}</button>
                    <button className="primary" disabled={checkPending !== null || state.cashCents < q.checkFlightFeeCents}
                            title={state.cashCents < q.checkFlightFeeCents ? 'Not enough cash' : ''}
                            onClick={() => beginCheck(q.class, q.displayName)}>
                      {q.held ? 'Re-test' : 'Begin check-flight'}
                    </button>
                  </div>
                  {centresFor === q.class && (
                    <div className="centres">
                      {centres.length === 0 ? <div className="hint muted">No test centres in range.</div> : (
                        <table className="tbl centres-tbl">
                          <thead><tr><th>Field</th><th>City</th><th className="r">Distance</th><th>Test aircraft</th><th className="r">Cost</th><th></th></tr></thead>
                          <tbody>{centres.map(c => (
                            <tr key={c.icao}>
                              <td className="loc">{c.icao}</td>
                              <td className="muted">{c.name}</td>
                              <td className="r num">{c.distanceNm === 0 ? 'here' : `${c.distanceNm} nm`}</td>
                              <td className="muted">{c.testAircraft}</td>
                              <td className="r num">{money(c.feeCents)}</td>
                              <td className="r"><button className="primary small" disabled={checkPending !== null || state.cashCents < c.feeCents} onClick={() => beginCheck(q.class, `${q.displayName} at ${c.icao}`)}>Test here</button></td>
                            </tr>
                          ))}</tbody>
                        </table>
                      )}
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
      </section>
    </div>
  )
}

function CheckFlightCard({ result }: { result: CheckFlightDone }) {
  return (
    <section className={`card settled-card ${result.passed ? '' : 'failed-card'}`}>
      <h2>{result.passed ? 'Check-flight passed ✓' : 'Check-flight failed'}</h2>
      <div className="settled-meta">
        <span>{result.className}</span>
        {result.passed && <span className="pos">{'★'.repeat(result.stars)}</span>}
        <span>touchdown <b className="num">{signed(Math.round(result.touchdownFpm))} fpm</b> ({landingWord(result.touchdownFpm)})</span>
        <span className="neg">−{money(result.feeCents)} fee</span>
      </div>
      {!result.passed && <div className="hint">Too firm — a check-flight needs ≤ 200 fpm. Book another attempt when you're ready.</div>}
    </section>
  )
}

// Phase 12 — the flight score + coaching debrief, shared by the logbook detail AND the end-of-flight card so the
// game's best feature (the un-gameable score + instructor debrief) lands at the moment you actually land.
function FlightScoreDebrief({ d }: { d: FlightDetail }) {
  return (
    <>
      {d.overallScore != null && (
        <div className="flt-scores">
          <div className="fs-overall">
            <span className="metalabel">Flight score{d.scoreValid === false ? ' · voided' : ''}</span>
            <span className={`fs-big num ${d.scoreValid === false ? 'neg' : scoreTone(d.overallScore)}`}>{d.overallScore}</span>
          </div>
          <div className="fs-subs">
            <div><span className="metalabel">Landing</span><span className={`num ${scoreTone(d.landingScore)}`}>{d.landingScore ?? '—'}</span></div>
            <div><span className="metalabel">Approach</span><span className={`num ${d.stabilizedApproach === false ? 'neg' : scoreTone(d.approachScore)}`}>{d.approachScore ?? '—'}{d.stabilizedApproach === false ? ' · unstable' : ''}</span></div>
            {d.comfortScore != null && <div><span className="metalabel">Comfort</span><span className={`num ${scoreTone(d.comfortScore)}`}>{d.comfortScore}</span></div>}
            {d.touchdownG != null && <div><span className="metalabel">Touchdown g</span><span className="num">{d.touchdownG.toFixed(2)}</span></div>}
            {d.violationPoints != null && d.violationPoints > 0 && <div><span className="metalabel">Exceedances</span><span className="num neg">−{d.violationPoints}</span></div>}
          </div>
        </div>
      )}
      {d.debrief.scored && (d.debrief.strengths.length > 0 || d.debrief.toImprove.length > 0) && (
        <div className="flt-debrief">
          <div className="metalabel flt-pay-head">Debrief · {d.debrief.grade}</div>
          <p className="dbf-headline">{d.debrief.headline}</p>
          {d.debrief.strengths.length > 0 && (
            <div className="dbf-group">
              <div className="dbf-grouphead pos">What went well</div>
              {d.debrief.strengths.map((n, i) => (
                <div className="dbf-note strength" key={i}>
                  <div className="dbf-note-head">{n.headline} <span className="dbf-dim">{n.dimension}</span></div>
                  {n.detail && <div className="dbf-note-detail">{n.detail}</div>}
                </div>
              ))}
            </div>
          )}
          {d.debrief.toImprove.length > 0 && (
            <div className="dbf-group">
              <div className="dbf-grouphead">To improve</div>
              {d.debrief.toImprove.map((n, i) => (
                <div className={`dbf-note ${n.tone.toLowerCase()}`} key={i}>
                  <div className="dbf-note-head">{n.headline} <span className="dbf-dim">{n.dimension}</span></div>
                  {n.detail && <div className="dbf-note-detail">{n.detail}</div>}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </>
  )
}

function SettlementCard({ settled }: { settled: Settled }) {
  // Phase 12 — pull the just-settled flight so the coaching debrief lands right here, at the emotional peak of
  // the flight, instead of a tab away in the logbook. The debrief is computed on /api/flights/{id} already.
  const [flight, setFlight] = useState<FlightDetail | null>(null)
  useEffect(() => {
    let live = true
    if (settled.flightId) api.flight(settled.flightId).then(f => { if (live) setFlight(f) }).catch(() => {})
    return () => { live = false }
  }, [settled.flightId])
  return (
    <section className="card settled-card">
      <h2>Job settled ✓</h2>
      <div className="settled-pay num">{money(settled.payoutCents)}</div>
      <div className="settled-meta">
        <span>+{settled.xp} XP</span>
        <span>touchdown <b className="num">{signed(Math.round(settled.touchdownFpm))} fpm</b> ({landingWord(settled.touchdownFpm)})</span>
        {settled.payloadMatched && <span className="pos">aircraft bonus</span>}
      </div>
      {settled.promotedTo && <div className="promo">🎖 Promoted to {settled.promotedTo}!</div>}
      {flight && <FlightScoreDebrief d={flight} />}
    </section>
  )
}

// ─── Aircraft imagery (Phase 6b) ─────────────────────────────────────────────

// The aircraft's own thumbnail from your installed sim; falls back to an original category silhouette
// (so it looks intentional on a machine with no MSFS, or for an aircraft that ships no thumbnail).
function AircraftImage({ typeId, category, mini }: { typeId?: string; category?: string; mini?: boolean }) {
  const [failed, setFailed] = useState(false)
  const [credit, setCredit] = useState<string | null>(null)
  useEffect(() => {
    setFailed(false); setCredit(null)
    if (!typeId || mini) return // caption only on the larger cards, and never for your own local thumbnail
    let live = true
    api.aircraftImageMeta(typeId)
      .then(m => { if (live && m?.attribution) setCredit(`${m.attribution}${m.license ? ' · ' + m.license : ''}`) })
      .catch(() => {})
    return () => { live = false }
  }, [typeId, mini])
  return (
    <div className={`ac-img ${mini ? 'mini' : ''}`}>
      {typeId && !failed
        ? <img src={api.aircraftImageUrl(typeId)} alt="" loading="lazy" onError={() => setFailed(true)} />
        : <AircraftSilhouette category={category} />}
      {credit && !failed && <span className="ac-credit" title={credit}>{credit}</span>}
    </div>
  )
}

function AircraftSilhouette({ category }: { category?: string }) {
  return (
    <svg viewBox="0 0 200 200" className="ac-sil" aria-hidden="true">
      {category === 'Helicopter'
        ? <g>
            <circle className="disc" cx="100" cy="90" r="74" />
            <ellipse cx="100" cy="92" rx="20" ry="42" />
            <rect x="96" y="126" width="8" height="58" rx="3" />
            <rect x="85" y="176" width="30" height="7" rx="3" />
          </g>
        : <path d="M100 8 C104 8 106 16 106 30 L106 74 L150 96 C154 98 155 100 155 104 L155 112 C155 115 152 115 149 114 L106 100 L106 150 L124 162 C127 164 127 168 124 168 L106 162 L106 178 C106 188 104 192 100 192 C96 192 94 188 94 178 L94 162 L76 168 C73 168 73 164 76 162 L94 150 L94 100 L51 114 C48 115 45 115 45 112 L45 104 C45 100 46 98 50 96 L94 74 L94 30 C94 16 96 8 100 8 Z" />}
    </svg>
  )
}

// A circular condition gauge (hull / engine) — the signature of the re-crafted hangar. conditionMilli
// is percent × 1000, so milli/1000 is the percentage (matches the rest of the app).
function ConditionRing({ label, milli }: { label: string; milli: number }) {
  const pct = Math.max(0, Math.min(100, milli / 1000))
  const r = 22
  const circ = 2 * Math.PI * r
  const tone = pct < 40 ? 'neg' : pct < 70 ? 'warn' : 'pos'
  return (
    <div className="cring">
      <svg viewBox="0 0 56 56" className={`cring-svg ${tone}`}>
        <circle className="cring-track" cx="28" cy="28" r={r} />
        <circle className="cring-arc" cx="28" cy="28" r={r}
          strokeDasharray={circ} strokeDashoffset={circ * (1 - pct / 100)} transform="rotate(-90 28 28)" />
        <text x="28" y="28" className="cring-pct num">{Math.round(pct)}</text>
      </svg>
      <div className="cring-label">{label}</div>
    </div>
  )
}

// One owned aircraft as a premium card: full imagery, hull + engine condition rings, and its papers.
// One owned aircraft as a premium, selectable card: imagery, hull + engine rings, value, papers.
function FleetCard({ a, selected, busy, onSelect, onMaintain }: {
  a: OwnedAircraft; selected: boolean; busy: boolean; onSelect: (a: OwnedAircraft) => void; onMaintain: (a: OwnedAircraft) => void
}) {
  const avail = a.availability === 'Available'
  return (
    <div className={`card fleet-card ${selected ? 'sel' : ''}`} onClick={() => onSelect(a)} role="button" tabIndex={0}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(a) } }}>
      <AircraftImage typeId={a.typeId} category={a.category} />
      <div className="fleet-head">
        <div className="fleet-idy">
          <div className="fleet-name">{a.name}</div>
          <div className="fleet-tail loc">{a.tail}<span className="muted"> · {spaced(a.category)}</span></div>
        </div>
        <div className="fleet-chips">
          <span className={`avail-pill ${avail ? 'ok' : ''}`}>{avail ? 'Available' : spaced(a.availability)}</span>
          {a.insured && <span className="insured-chip" title="Insured">◈ Insured</span>}
        </div>
      </div>
      <div className="fleet-mid">
        <ConditionRing label="Hull" milli={a.hullConditionMilli} />
        <ConditionRing label="Engine" milli={a.engineConditionMilli} />
        <div className="fleet-facts">
          <div><span className="metalabel">Based</span><span className="loc">{a.locationIcao}</span></div>
          <div><span className="metalabel">Airframe</span><span className="num">{a.airframeHours.toFixed(1)} h</span></div>
          <div><span className="metalabel">Rating</span>{a.rated ? <span className="pos">Rated</span> : <span className="muted" title={`Needs ${a.requiredClass}`}>🔒 {a.requiredClass}</span>}</div>
        </div>
      </div>
      <div className="fleet-extra">
        <span><span className="metalabel">Value</span> <span className="num">{money(a.resaleValueCents)}</span></span>
        <span><span className="metalabel">Earned</span> <span className="num pos">{money(a.lifetimeEarningsCents)}</span></span>
      </div>
      <div className="fleet-foot">
        {a.maintenanceDue
          ? <><span className="warn-text">● Maintenance due</span><button className="primary small" disabled={busy} onClick={e => { e.stopPropagation(); onMaintain(a) }}>Service · {money(a.maintenanceQuoteCents)}</button></>
          : <><span className="muted">Next service</span><span className="muted num">{money(a.maintenanceQuoteCents)}</span></>}
      </div>
    </div>
  )
}

// A short maintenance-interval meter: how many hours are left before the next service is due.
function MaintMeter({ a }: { a: OwnedAircraft }) {
  const used = Math.max(0, a.maintenanceIntervalHours - a.hoursToService)
  const pct = a.maintenanceIntervalHours > 0 ? Math.min(100, (used / a.maintenanceIntervalHours) * 100) : 0
  const tone = a.maintenanceDue ? 'neg' : a.hoursToService < a.maintenanceIntervalHours * 0.25 ? 'warn' : 'pos'
  return (
    <div className="maint-meter">
      <div className="maint-track"><div className={`maint-fill ${tone}`} style={{ width: `${Math.max(3, pct)}%` }} /></div>
      <div className="maint-cap">
        {a.maintenanceDue
          ? <span className="warn-text">Service overdue</span>
          : <span className="muted"><span className="num">{a.hoursToService.toFixed(1)} h</span> to next service</span>}
      </div>
    </div>
  )
}

// The reusable drill-down: hero image, full spec sheet, condition + maintenance, per-airframe economics,
// flight history, and every action for one tail. This is the detail-panel pattern the Hangar establishes.
function AircraftDetail({ a, history, bases, crew, busy, onService, onInspect, onInsure, onRelocate, onSell, onAssignPilot }: {
  a: OwnedAircraft; history: AircraftHistory | null; bases: BaseView[]; crew: Staff[]; busy: boolean
  onService: (a: OwnedAircraft) => void; onInspect: (a: OwnedAircraft) => void; onInsure: (a: OwnedAircraft) => void
  onRelocate: (a: OwnedAircraft, dest: string) => void; onSell: (a: OwnedAircraft) => void
  onAssignPilot: (a: OwnedAircraft, staffId: string | null) => void
}) {
  const [dest, setDest] = useState('')
  const [confirmSell, setConfirmSell] = useState(false)
  useEffect(() => { setDest(''); setConfirmSell(false) }, [a.id])

  const avail = a.availability === 'Available'
  const eco = history?.economics
  const ferryTargets = bases.filter(b => b.icao !== a.locationIcao)
  const destBase = ferryTargets.find(b => b.icao === dest)
  const ferryNm = destBase && (a.lat || a.lon) ? distNm([a.lat, a.lon], [destBase.latitude, destBase.longitude]) : 0
  const ferryEst = destBase ? 30000 + Math.round(ferryNm * 350) : 0 // mirrors AircraftFerry{Base,PerNm}Cents
  const netVsBuy = eco ? eco.operatingNetCents - eco.purchasePriceCents : 0

  const spec: [string, string | null][] = [
    ['Manufacturer', a.manufacturer],
    ['ICAO type', a.icaoTypeDesignator],
    ['Model', a.icaoModel],
    ['Class', spaced(a.category)],
    ['Seats', a.seats != null ? String(a.seats) : null],
    ['Useful load', a.usefulLoadLbs != null ? `${a.usefulLoadLbs.toLocaleString()} lb` : null],
    ['Fuel cap', a.fuelCapacityLbs != null ? `${a.fuelCapacityLbs.toLocaleString()} lb` : null],
    ['Cruise', a.cruiseKtas != null ? `${a.cruiseKtas} kt` : null],
    ['Min runway', a.minRunwayFt != null ? `${a.minRunwayFt.toLocaleString()} ft` : null],
    ['Installed', a.onDisk ? 'On this sim' : 'Not installed'],
  ]

  const points: MapPoint[] = (a.lat || a.lon) ? [{ lat: a.lat, lon: a.lon, label: a.tail, kind: 'home' }] : []

  return (
    <div className="card acd">
      <div className="acd-hero">
        <div className="acd-shot"><AircraftImage typeId={a.typeId} category={a.category} /></div>
        <div className="acd-idy">
          <div className="acd-name">{a.name}</div>
          <div className="acd-tail loc">{a.tail} · {spaced(a.category)}</div>
          <div className="acd-badges">
            <span className={`avail-pill ${avail ? 'ok' : ''}`}>{avail ? 'Available' : spaced(a.availability)}</span>
            {a.insured
              ? <span className="insured-chip" title={a.insuredValueCents != null ? `Covers ${money(a.insuredValueCents)}` : 'Insured'}>◈ Insured{a.coverageMilli != null ? ` · ${Math.round(a.coverageMilli / 1000)}%` : ''}</span>
              : <span className="uninsured-chip">Uninsured</span>}
            {!a.rated && <span className="rate-chip" title={`Needs ${a.requiredClass}`}>🔒 {a.requiredClass}</span>}
          </div>
          <div className="acd-worth">
            <div><span className="metalabel">Resale value</span><span className="num">{money(a.resaleValueCents)}</span></div>
            <div><span className="metalabel">Market</span><span className="num muted">{money(a.marketValueCents)}</span></div>
            <div><span className="metalabel">Paid</span><span className="num muted">{a.purchasePriceCents != null ? money(a.purchasePriceCents) : '—'}</span></div>
            <div><span className="metalabel">Acquired</span><span className="num muted">{a.acquiredAt ? when(a.acquiredAt) : '—'}</span></div>
          </div>
        </div>
      </div>

      <div className="acd-cond">
        <ConditionRing label="Hull" milli={a.hullConditionMilli} />
        <ConditionRing label="Engine" milli={a.engineConditionMilli} />
        <div className="acd-cond-body">
          <div className="acd-cond-row"><span className="metalabel">Airframe hours</span><span className="num">{a.airframeHours.toFixed(1)} h</span></div>
          <MaintMeter a={a} />
          <div className="acd-cond-row"><span className="metalabel">Next service</span><span className="num">{money(a.maintenanceQuoteCents)}</span></div>
          <div className="acd-cond-row"><span className="metalabel">100-hour</span><span className={`num ${a.hoursTo100h <= 0 ? 'neg' : ''}`}>{a.hoursTo100h <= 0 ? 'overdue' : `in ${Math.round(a.hoursTo100h)} h`}</span></div>
          <div className="acd-cond-row"><span className="metalabel">Annual</span><span className={`num ${a.daysToAnnual <= 0 ? 'neg' : ''}`}>{a.daysToAnnual <= 0 ? 'overdue' : `in ${a.daysToAnnual} d`}</span></div>
        </div>
      </div>
      {!a.airworthy && <div className="banner error acd-grounded">Grounded — {a.unairworthyReason}. It can't be dispatched until cleared.</div>}

      <h3 className="sub-h">Spec sheet</h3>
      <div className="spec-sheet">
        {spec.filter(([, v]) => v != null).map(([k, v]) => (
          <div key={k} className="metacell"><span className="metalabel">{k}</span><span className="num">{v}</span></div>
        ))}
      </div>

      <h3 className="sub-h">Operating economics</h3>
      {eco ? (
        <>
          <div className="eco-grid">
            <Meta label="Lifetime earned" value={money(eco.lifetimeEarningsCents)} />
            <Meta label="Legs flown" value={String(eco.lifetimeFlights)} />
            <Meta label="Distance" value={`${Math.round(eco.lifetimeDistanceNm).toLocaleString()} nm`} />
            <Meta label="Fuel burned" value={`${Math.round(eco.lifetimeFuelLbs).toLocaleString()} lb`} />
            <Meta label="Per leg" value={money(eco.avgEarningsPerFlightCents)} />
            <Meta label="Net / hour" value={money(eco.operatingNetPerHourCents)} />
            <Meta label="Avg touchdown" value={eco.lifetimeFlights ? `${Math.round(eco.avgTouchdownFpm)} fpm` : '—'} />
            <Meta label="Net vs. purchase" value={money(netVsBuy)} />
          </div>
          <div className="eco-bars">
            <BarRow label="Earned" value={eco.lifetimeEarningsCents} max={eco.lifetimeEarningsCents || 1} tone="pos" />
            <BarRow label="Repairs" value={-eco.lifetimeRepairCents} max={eco.lifetimeEarningsCents || 1} tone="neg" />
            <BarRow label="Fuel / ferry" value={-eco.lifetimeFuelCents} max={eco.lifetimeEarningsCents || 1} tone="neg" />
            <BarRow label="Insurance" value={-eco.lifetimeInsuranceCents} max={eco.lifetimeEarningsCents || 1} tone="neg" />
            <BarRow label="Operating net" value={eco.operatingNetCents} max={eco.lifetimeEarningsCents || 1} tone={eco.operatingNetCents >= 0 ? 'pos' : 'neg'} />
          </div>
        </>
      ) : <div className="chart-empty">Loading history…</div>}

      <h3 className="sub-h">Where it sits</h3>
      <SatelliteMap points={points} />
      <div className="acd-loc muted">At <span className="loc">{a.locationIcao}</span> · {a.locationName}</div>

      {a.ownership !== 'Rented' && (
        <div className="acd-pilot">
          <span className="metalabel">Assigned pilot</span>
          <select value={a.assignedStaffId ?? ''} disabled={busy} onChange={e => onAssignPilot(a, e.target.value || null)}>
            <option value="">You / anyone</option>
            {crew.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
          <span className="muted">{a.assignedStaffName ? `${a.assignedStaffName}'s aircraft` : 'open to any pilot'}</span>
        </div>
      )}

      <h3 className="sub-h">Flight history {history && <span className="muted">· {history.flights.length}</span>}</h3>
      {history && history.flights.length > 0 ? (
        <div className="tbl-wrap"><table className="tbl hist-table">
          <thead><tr><th>When</th><th>Leg</th><th className="r">Dist</th><th className="r">Fuel</th><th>Landing</th><th className="r">Payout</th><th className="r">XP</th></tr></thead>
          <tbody>{history.flights.map(f => (
            <tr key={f.id}>
              <td className="muted">{when(f.settledAt)}</td>
              <td>{f.origin && f.dest ? <><span className="loc">{f.origin}</span> → <span className="loc">{f.dest}</span></> : <span className="muted">—</span>}</td>
              <td className="r num">{Math.round(f.distanceNm)}</td>
              <td className="r num">{Math.round(f.fuelUsedLbs)}</td>
              <td><span className={`land ${landingWord(f.touchdownFpm)}`}>{landingWord(f.touchdownFpm)}</span> <span className="muted num">{Math.round(Math.abs(f.touchdownFpm))}</span></td>
              <td className="r num pos">{money(f.payoutCents)}</td>
              <td className="r num">+{f.xp}</td>
            </tr>
          ))}</tbody>
        </table></div>
      ) : <div className="empty">No flights logged on this airframe yet.</div>}

      <div className="acd-actions">
        <button className="primary" disabled={busy || (!a.maintenanceDue && a.hullConditionMilli >= 100000 && a.engineConditionMilli >= 100000)}
          onClick={() => onService(a)} title={a.maintenanceDue ? 'Service due' : 'Restore to full condition'}>
          Service · {money(a.maintenanceQuoteCents)}
        </button>
        {a.inspectionQuoteCents > 0 && (
          <button className="primary" disabled={busy} onClick={() => onInspect(a)} title="Clear the due 100-hour / annual inspections">
            Inspect · {money(a.inspectionQuoteCents)}
          </button>
        )}
        {a.ownership === 'Owned' && !a.insured && <button disabled={busy} onClick={() => onInsure(a)}>Insure</button>}
        {ferryTargets.length > 0 && (
          <div className="relocate-form">
            <select value={dest} onChange={e => setDest(e.target.value)} disabled={busy || !avail}>
              <option value="">Ferry to base…</option>
              {ferryTargets.map(b => <option key={b.icao} value={b.icao}>{b.icao} · {b.name}</option>)}
            </select>
            <button disabled={busy || !avail || !dest} onClick={() => onRelocate(a, dest)}>
              Ferry{destBase ? ` · ~${money(ferryEst)}` : ''}
            </button>
          </div>
        )}
        {a.ownership === 'Owned' && (confirmSell
          ? <><button className="danger" disabled={busy || !avail} onClick={() => onSell(a)}>Confirm sell · {money(a.resaleValueCents)}</button>
              <button disabled={busy} onClick={() => setConfirmSell(false)}>Cancel</button></>
          : <button className="danger-ghost" disabled={busy || !avail} onClick={() => setConfirmSell(true)}
              title={avail ? 'Sell this airframe' : 'Must be available to sell'}>Sell</button>)}
      </div>
      {a.ownership === 'Rented' && <div className="hint">Rented — fly it, then return it from the Rentals section below. It can't be sold or insured.</div>}
      {!avail && <div className="hint">Ferry and sale are available only when the aircraft is idle.</div>}
    </div>
  )
}

// ─── Hangar (own aircraft) ───────────────────────────────────────────────────

type FleetSort = 'value' | 'hours' | 'condition' | 'earned' | 'name'

function Hangar({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [owned, setOwned] = useState<OwnedAircraft[] | null>(null)
  const [offers, setOffers] = useState<AircraftOffer[] | null>(null)
  const [used, setUsed] = useState<UsedListing[]>([])
  const [rentalOffers, setRentalOffers] = useState<RentalOffer[]>([])
  const [rentals, setRentals] = useState<ActiveRental[]>([])
  const [leases, setLeases] = useState<ActiveLease[]>([]) // existing lease agreements only — leasing is retired from the market
  const [bases, setBases] = useState<BaseView[]>([])
  const [crew, setCrew] = useState<Staff[]>([]) // Phase 13 — for assigning a tail to a hired pilot
  const [selId, setSelId] = useState<string | null>(null)
  const [history, setHistory] = useState<AircraftHistory | null>(null)
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()
  const [q, setQ] = useState('')
  const [sort, setSort] = useState<FleetSort>('value')
  // Aircraft-market filters
  const [mktQ, setMktQ] = useState('')
  const [mktCat, setMktCat] = useState('')            // '' = every category
  const [mktRentOnly, setMktRentOnly] = useState(false)
  const [mktSort, setMktSort] = useState<'price' | 'seats' | 'payload' | 'distance'>('distance')

  const load = useCallback(async () => {
    try {
      const h = await api.hangar()
      setOwned(h)
      setOffers(await api.market())
      setUsed(await api.usedMarket())
      setRentalOffers(await api.rentalOffers())
      setRentals(await api.rentals())
      setLeases(await api.leases())
      setBases(await api.bases())
      setCrew(await api.staff().catch(() => []))
      setSelId(prev => (prev && h.some(a => a.id === prev)) ? prev : (h[0]?.id ?? null))
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  // Pull the drill-down whenever the selection changes.
  useEffect(() => {
    if (!selId) { setHistory(null); return }
    let live = true
    setHistory(null)
    api.aircraftHistory(selId).then(h => { if (live) setHistory(h) }).catch(() => { if (live) setHistory(null) })
    return () => { live = false }
  }, [selId, owned])

  const delivery = (icao: string, distNm: number) =>
    distNm <= 1 ? `It's at ${icao}, right where you are.` : `It's parked at ${icao} (${Math.round(distNm)} nm away) — fly it home or ferry it from the Hangar.`
  const buy = async (o: AircraftOffer) => {
    setBusy(true); setMsg(null)
    try { await api.buyAircraft(o.typeId); await load(); onChanged(); setMsg(`Bought a ${o.name}. ${delivery(o.locationIcao, o.distanceNm)}`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const buyUsedListing = async (l: UsedListing) => {
    setBusy(true); setMsg(null)
    try { await api.buyUsed(l.typeId, l.seed); await load(); onChanged(); setMsg(`Bought a used ${l.typeName} (${Math.round(l.airframeHours)} h, ${Math.round(l.conditionMilli / 1000)}% condition). ${delivery(l.locationIcao, l.distanceNm)}`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const rentAircraft = async (o: RentalOffer) => {
    setBusy(true); setMsg(null)
    try { await api.rent(o.typeId); await load(); onChanged(); setMsg(`Rented a ${o.typeName} — it's in your hangar. Fly it, then return it.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const returnRentalAgreement = async (r: ActiveRental) => {
    setBusy(true); setMsg(null)
    try { const res = await api.returnRental(r.agreementId); await load(); onChanged(); setMsg(`Returned ${r.tail} — ${money(res.refundCents)} deposit back.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const returnLeaseAgreement = async (l: ActiveLease) => {
    setBusy(true); setMsg(null)
    try { const res = await api.returnLease(l.agreementId); await load(); onChanged(); setMsg(`Returned ${l.tail} — ${money(res.refundCents)} deposit back.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const buyoutLeaseAgreement = async (l: ActiveLease) => {
    setBusy(true); setMsg(null)
    try { const res = await api.buyoutLease(l.agreementId); await load(); onChanged(); setMsg(`Bought out ${l.tail} for ${money(res.buyoutCents)} — it's yours now.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const casualtyLeaseAgreement = async (l: ActiveLease) => {
    setBusy(true); setMsg(null)
    try { const res = await api.casualtyLease(l.agreementId); await load(); onChanged(); setMsg(`${l.tail} written off — you paid the ${money(res.deductibleCents)} deductible, deposit refunded.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const maintain = async (a: OwnedAircraft) => {
    setBusy(true); setMsg(null)
    try { await api.maintain(a.id); await load(); onChanged(); setMsg(`Serviced ${a.tail} — good as new.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const inspect = async (a: OwnedAircraft) => {
    setBusy(true); setMsg(null)
    try { await api.inspect(a.id); await load(); onChanged(); setMsg(`${a.tail} inspected and returned to service.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const insure = async (a: OwnedAircraft) => {
    setBusy(true); setMsg(null)
    try { await api.insure(a.id); await load(); onChanged(); setMsg(`${a.tail} is insured.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const relocate = async (a: OwnedAircraft, destIcao: string) => {
    setBusy(true); setMsg(null)
    try { const r = await api.relocateAircraft(a.id, destIcao); await load(); onChanged(); setMsg(`Ferried ${a.tail} to ${destIcao} for ${money(r.feeCents)}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const sell = async (a: OwnedAircraft) => {
    setBusy(true); setMsg(null)
    try { const r = await api.sellAircraft(a.id); setSelId(null); await load(); onChanged(); setMsg(`Sold ${a.tail} for ${money(r.proceedsCents)}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const assignPilot = async (a: OwnedAircraft, staffId: string | null) => {
    setBusy(true); setMsg(null)
    try { await api.assignPilot(a.id, staffId); await load(); onChanged(); const who = crew.find(c => c.id === staffId)?.name; setMsg(who ? `${a.name} assigned to ${who}.` : `${a.name} is open to any pilot.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const fleet = owned ?? []
  const filtered = fleet.filter(a => {
    const s = q.trim().toLowerCase()
    return !s || a.tail.toLowerCase().includes(s) || a.name.toLowerCase().includes(s)
      || a.locationIcao.toLowerCase().includes(s) || spaced(a.category).toLowerCase().includes(s)
  })
  const condOf = (a: OwnedAircraft) => Math.min(a.hullConditionMilli, a.engineConditionMilli)
  const sorted = [...filtered].sort((x, y) => {
    switch (sort) {
      case 'value': return y.resaleValueCents - x.resaleValueCents
      case 'hours': return y.airframeHours - x.airframeHours
      case 'condition': return condOf(x) - condOf(y)          // worst first — what needs you
      case 'earned': return y.lifetimeEarningsCents - x.lifetimeEarningsCents
      case 'name': return x.name.localeCompare(y.name)
    }
  })
  const sel = fleet.find(a => a.id === selId) ?? null

  // Fleet KPIs — the operating numbers, surfaced at a glance instead of buried a drill-down away.
  const totalValue = fleet.filter(a => a.ownership === 'Owned').reduce((s, a) => s + a.resaleValueCents, 0) // rentals aren't assets (Phase 9f)
  const totalHours = fleet.reduce((s, a) => s + a.airframeHours, 0)
  const totalEarned = fleet.reduce((s, a) => s + a.lifetimeEarningsCents, 0)
  const availCount = fleet.filter(a => a.availability === 'Available').length
  const dueCount = fleet.filter(a => a.maintenanceDue).length
  const avgCond = fleet.length ? Math.round(fleet.reduce((s, a) => s + condOf(a), 0) / fleet.length / 1000) : 0

  const sorts: [FleetSort, string][] = [['value', 'Value'], ['earned', 'Earned'], ['hours', 'Hours'], ['condition', 'Needs service'], ['name', 'Name']]
  // One unified market: for each buyable type, whether it can also be rented (badge + action).
  const rentByType = new Map((rentalOffers ?? []).map(o => [o.typeId, o]))
  const mktCats = Array.from(new Set((offers ?? []).map(o => o.category)))
  const shownOffers = (offers ?? [])
    .filter(o => { const s = mktQ.trim().toLowerCase(); return !s || o.name.toLowerCase().includes(s) || spaced(o.category).toLowerCase().includes(s) || o.locationIcao.toLowerCase().includes(s) })
    .filter(o => !mktCat || o.category === mktCat)
    .filter(o => !mktRentOnly || rentByType.has(o.typeId))
    .sort((a, b) => mktSort === 'price' ? a.priceCents - b.priceCents
      : mktSort === 'seats' ? (b.seats ?? 0) - (a.seats ?? 0)
      : mktSort === 'payload' ? (b.usefulLoadLbs ?? 0) - (a.usefulLoadLbs ?? 0)
      : a.distanceNm - b.distanceNm)

  return (
    <div className="hangar-screen">
      <section className="card">
        <div className="row-head"><h2>Your hangar <span className="muted">· {fleet.length} {fleet.length === 1 ? 'tail' : 'tails'}</span></h2></div>
                {owned === null ? <div className="empty">Loading…</div>
          : fleet.length === 0 ? <div className="empty">No aircraft yet — buy one below.</div>
          : (
            <>
              <div className="fleet-kpis">
                <HeroStat label="Fleet value" value={money(totalValue)} accent />
                <HeroStat label="Lifetime earned" value={money(totalEarned)} />
                <HeroStat label="Airframe hours" value={totalHours.toFixed(0)} unit="h" />
                <HeroStat label="Available" value={`${availCount}/${fleet.length}`} />
                <HeroStat label="Avg condition" value={String(avgCond)} unit="%" />
                <HeroStat label="Service due" value={String(dueCount)} />
              </div>

              <div className="hangar-toolbar">
                <input className="hangar-search" placeholder="Search tail, type, or base…" value={q} onChange={e => setQ(e.target.value)} />
                <div className="hangar-sort">
                  {sorts.map(([k, lbl]) => (
                    <button key={k} type="button" className={`hsort ${sort === k ? 'on' : ''}`} onClick={() => setSort(k)}>{lbl}</button>
                  ))}
                </div>
              </div>

              <div className="fleet">
                {sorted.map(a => <FleetCard key={a.id} a={a} selected={a.id === selId} busy={busy} onSelect={x => setSelId(x.id)} onMaintain={maintain} />)}
              </div>
              {sorted.length === 0 && <div className="empty">No aircraft match “{q}”.</div>}
            </>
          )}
      </section>

      {sel && (
        <section className="card acd-wrap">
          <AircraftDetail a={sel} history={history} bases={bases} crew={crew} busy={busy}
            onService={maintain} onInspect={inspect} onInsure={insure} onRelocate={relocate} onSell={sell} onAssignPilot={assignPilot} />
        </section>
      )}

      <section className="card">
        <div className="row-head"><h2>Aircraft market</h2><span className="hint">buy outright or rent by the hour</span></div>
        {offers !== null && offers.length > 0 && (
          <div className="mkt-filters">
            <input className="mkt-search" placeholder="Search name, class or field…" value={mktQ} onChange={e => setMktQ(e.target.value)} />
            <select value={mktCat} onChange={e => setMktCat(e.target.value)}>
              <option value="">All classes</option>
              {mktCats.map(c => <option key={c} value={c}>{spaced(c)}</option>)}
            </select>
            <select value={mktSort} onChange={e => setMktSort(e.target.value as typeof mktSort)}>
              <option value="distance">Nearest first</option>
              <option value="price">Cheapest first</option>
              <option value="seats">Most seats</option>
              <option value="payload">Most payload</option>
            </select>
            <button type="button" className={`mkt-toggle ${mktRentOnly ? 'on' : ''}`} onClick={() => setMktRentOnly(v => !v)}>Rentable only</button>
          </div>
        )}
        {offers === null ? <div className="empty">Loading…</div>
          : offers.length === 0 ? <div className="empty">No aircraft types known yet.</div>
          : shownOffers.length === 0 ? <div className="empty">No aircraft match those filters.</div>
          : (
            <div className="ac-market">
              {shownOffers.map(o => {
                const afford = state.cashCents >= o.priceCents
                const rent = rentByType.get(o.typeId)
                return (
                  <div className="card job ac-row" key={o.typeId}>
                    <AircraftImage typeId={o.typeId} category={o.category} />
                    <div className="ac-info">
                      <div className="job-top">
                        <div className="leg"><b>{o.name}</b></div>
                        {o.onDisk && <div className="tag">installed</div>}
                      </div>
                      <div className="commodity">{spaced(o.category)}</div>
                      <div className="ac-caps">
                        <span className="cap buy">Buy</span>
                        {rent && <span className="cap rent">Rentable</span>}
                      </div>
                      <div className="job-meta">
                        {o.seats != null && <Meta label="Seats" value={String(o.seats)} />}
                        {o.usefulLoadLbs != null && <Meta label="Payload" value={`${o.usefulLoadLbs.toLocaleString()} lb`} />}
                        {o.cruiseKtas != null && <Meta label="Cruise" value={`${o.cruiseKtas} kt`} />}
                      </div>
                      <div className="ac-loc" title="Buying takes delivery here — you fly it home or ferry it.">
                        <span className="loc">{o.locationIcao}</span>
                        <span className="muted"> · {o.locationName}</span>
                        <span className="ac-dist">{o.distanceNm <= 1 ? 'here' : `${Math.round(o.distanceNm)} nm`}</span>
                      </div>
                    </div>
                    <div className="ac-buy">
                      <div className="price num">{money(o.priceCents)}</div>
                      <button className="primary" disabled={busy || !afford} title={afford ? `Buy — delivered at ${o.locationIcao}` : 'over budget'} onClick={() => buy(o)}>Buy</button>
                      {rent && <button className="ghost small" disabled={busy || state.cashCents < rent.depositCents} title={`Deposit ${money(rent.depositCents)} · ${money(rent.flightHourCents)}/h`} onClick={() => rentAircraft(rent)}>Rent · {money(rent.depositCents)}</button>}
                    </div>
                  </div>
                )
              })}
            </div>
          )}
      </section>

      {used.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Used market</h2><span className="hint">Pre-owned — cheaper, but flown</span></div>
          <div className="ac-market">
            {used.map(l => {
              const afford = state.cashCents >= l.priceCents
              const off = Math.round((1 - l.priceCents / l.newPriceCents) * 100)
              return (
                <div className="card job ac-row" key={l.seed}>
                  <AircraftImage typeId={l.typeId} category={l.category} />
                  <div className="ac-info">
                    <div className="job-top"><div className="leg"><b>{l.typeName}</b></div><div className="tag">−{off}% vs new</div></div>
                    <div className="commodity">{spaced(l.category)}</div>
                    <div className="job-meta">
                      <Meta label="Hours" value={`${Math.round(l.airframeHours).toLocaleString()} h`} />
                      <Meta label="Condition" value={`${Math.round(l.conditionMilli / 1000)}%`} />
                    </div>
                    <div className="ac-loc" title="Buying takes delivery here — you fly it home or ferry it.">
                      <span className="loc">{l.locationIcao}</span>
                      <span className="muted"> · {l.locationName}</span>
                      <span className="ac-dist">{l.distanceNm <= 1 ? 'here' : `${Math.round(l.distanceNm)} nm`}</span>
                    </div>
                  </div>
                  <div className="ac-buy">
                    <div className="price num">{money(l.priceCents)} <span className="fair-ref">new {money(l.newPriceCents)}</span></div>
                    <span className="hint">{afford ? '' : 'over budget'}</span>
                    <button className="primary" disabled={busy || !afford} onClick={() => buyUsedListing(l)}>Buy used</button>
                  </div>
                </div>
              )
            })}
          </div>
        </section>
      )}

      {rentals.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Your rentals</h2><span className="hint">Fly by hand, then return — the deposit comes back less any real damage</span></div>
          <div className="jobs">
            {rentals.map(r => (
              <div className="card job" key={r.agreementId}>
                <div className="job-top"><div className="leg"><b>{r.tail}</b> · {r.typeName}</div><div className="tag">{r.daysLeft}d left</div></div>
                <div className="commodity">at {r.locationIcao}</div>
                <div className="job-meta">
                  <Meta label="Deposit" value={money(r.depositCents)} />
                  <Meta label="Rent so far" value={money(r.accruedRentCents)} />
                  <Meta label="Usage" value={`${money(r.flightHourCents)}/h`} />
                </div>
                <div className="price num">{money(r.projected.refundCents)} <span className="fair-ref">refund now</span></div>
                {r.projected.damageCents > 0 && <div className="hint">−{money(r.projected.damageCents)} damage: {r.projected.damageReason}</div>}
                <div className="job-foot">
                  <span className="hint" />
                  <button className="primary" disabled={busy} onClick={() => returnRentalAgreement(r)}>Return</button>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}


      {leases.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Your leases</h2><span className="hint">Weekly rate + hull cover — return it, buy it out, or (if written off) claim casualty</span></div>
          <div className="jobs">
            {leases.map(l => (
              <div className="card job" key={l.agreementId}>
                <div className="job-top"><div className="leg"><b>{l.tail}</b> · {l.typeName}</div><div className="tag">{l.daysLeft}d left</div></div>
                <div className="commodity">at {l.locationIcao}</div>
                <div className="job-meta">
                  <Meta label="Weekly" value={money(l.weeklyRateCents)} />
                  <Meta label="Hull cover" value={`${money(l.insuranceWeeklyCents)}/wk`} />
                  <Meta label="Deposit" value={money(l.depositCents)} />
                </div>
                <div className="price num">{money(l.buyoutCents)} <span className="fair-ref">to buy out</span></div>
                {l.projected.damageCents > 0 && <div className="hint">return now: −{money(l.projected.damageCents)} condition, {money(l.projected.refundCents)} back</div>}
                <div className="job-foot">
                  <span className="hint" />
                  <div className="relocate-form">
                    <button disabled={busy} onClick={() => returnLeaseAgreement(l)}>Return</button>
                    <button className="primary" disabled={busy} onClick={() => buyoutLeaseAgreement(l)}>Buy out</button>
                    {l.casualtyEligible && <button className="danger-ghost" disabled={busy} onClick={() => casualtyLeaseAgreement(l)}>Write off</button>}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

    </div>
  )
}

// Contract markups you can demand on a standing order. `fillFor` mirrors EconomyConfig.ContractFillProbability
// (sensitivity 0.6, floor 25%) so the form previews how a premium thins the client's fill rate.
const MARKUP_OPTS = [1000, 1100, 1250, 1400, 1500]
const markupLabel = (m: number) => (m === 1000 ? 'Fair' : `+${Math.round(m / 10 - 100)}%`)
const fillFor = (m: number) => (m <= 1000 ? 100 : Math.max(25, Math.round((1 - 0.6 * (m / 1000 - 1)) * 100)))

function Ops({ onChanged }: { onChanged: () => void }) {
  const [staff, setStaff] = useState<Staff[]>([])
  const [candidates, setCandidates] = useState<StaffCandidate[]>([])
  const [orders, setOrders] = useState<StandingOrder[]>([])
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [dests, setDests] = useState<{ icao: string; name: string }[]>([])
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()
  const [oStaff, setOStaff] = useState('')
  const [oAircraft, setOAircraft] = useState('')
  const [oDest, setODest] = useState('')
  const [oMarkup, setOMarkup] = useState(1000)
  const [dispatches, setDispatches] = useState<DispatchLeg[]>([])
  const [routes, setRoutes] = useState<RouteData | null>(null)
  const [rName, setRName] = useState('')
  const [rOrigin, setROrigin] = useState('')
  const [rDest, setRDest] = useState('')
  const [rStaff, setRStaff] = useState('')
  const [mgrBase, setMgrBase] = useState('') // Phase 12 — base to station a new manager
  const [rAircraft, setRAircraft] = useState('')
  const [rMission, setRMission] = useState('Cargo')
  const [rMarkup, setRMarkup] = useState(1000)
  const [rScheduled, setRScheduled] = useState(false) // Phase 11f — open a scheduled-passenger route instead

  const load = useCallback(async () => {
    try {
      setStaff(await api.staff())
      setCandidates(await api.staffCandidates())
      setOrders(await api.orders())
      setFleet((await api.hangar()).filter(h => h.availability === 'Available'))
      const uniq = new Map((await api.jobs()).map(j => [j.dest, j.destName]))
      setDests(Array.from(uniq, ([icao, name]) => ({ icao, name })))
      setRoutes(await api.routes())
      setDispatches(await api.dispatches())
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const createRoute = async () => {
    if (!rStaff || !rAircraft || !rOrigin || !rDest) { setMsg('A route needs a pilot, an aircraft, and two of your bases.'); return }
    setBusy(true); setMsg(null)
    try {
      if (rScheduled) {
        const r = await api.createScheduledRoute({ name: rName.trim() || undefined, originIcao: rOrigin, destIcao: rDest, aircraftInstanceId: rAircraft, staffId: rStaff })
        setMsg(`Scheduled service opened — ${r.seatCapacity} seats at ${Math.round(r.loadFactorMilli / 10)}% load.`)
      } else {
        await api.createRoute({ name: rName.trim() || undefined, originIcao: rOrigin, destIcao: rDest, aircraftInstanceId: rAircraft, staffId: rStaff, mission: rMission, priceMultiplierMilli: rMarkup })
        setMsg('Route opened — it earns while you fly.')
      }
      setRName(''); setROrigin(''); setRDest(''); setRStaff(''); setRAircraft(''); setRMarkup(1000); await load(); onChanged()
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const repriceRoute = async (id: string, milli: number) => {
    setBusy(true); setMsg(null)
    try { await api.setRoutePrice(id, milli); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const cancelDispatch = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.cancelDispatch(id); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const cancelRoute = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.cancelRoute(id); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const hire = async (c: StaffCandidate) => {
    setBusy(true); setMsg(null)
    try { await api.hire(c.seed); await load(); onChanged(); setMsg(`Hired ${c.name}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const hireManager = async (icao: string) => {
    setBusy(true); setMsg(null)
    try { await api.hireManager(icao); await load(); onChanged(); setMsg(`Hired a manager at ${icao} — they'll keep the fleet there serviced.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const relocateCrew = async (s: Staff) => {
    const dest = window.prompt(`Reposition ${s.name} — destination airport ICAO:`, s.currentIcao ?? '')?.trim().toUpperCase()
    if (!dest || dest === s.currentIcao) return
    setBusy(true); setMsg(null)
    try { const r = await api.relocateCrew(s.id, dest); await load(); onChanged(); setMsg(`Repositioned ${s.name} to ${dest} — ${money(r.feeCents)}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const dismiss = async (s: Staff) => {
    if (!window.confirm(`Let ${s.name} go? Their wage stops and this can't be undone.`)) return
    setBusy(true); setMsg(null)
    try { await api.dismissStaff(s.id); await load(); onChanged(); setMsg(`${s.name} has left the company.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  // Managers are staff too, but they run a base rather than fly — keep them out of the pilot pickers/roster.
  const pilots = staff.filter(s => s.role !== 'Manager')
  const managers = staff.filter(s => s.role === 'Manager')
  const managedIcaos = new Set(managers.map(m => m.currentIcao))
  const manageableBases = (routes?.bases ?? []).filter(b => !managedIcaos.has(b.icao))
  // Phase 12 — co-location: the pilot must be where the aircraft (standing order) / base (route) is.
  const oPilot = staff.find(s => s.id === oStaff)
  const oPlane = fleet.find(f => f.id === oAircraft)
  const oMismatch = oPilot?.currentIcao && oPlane && oPilot.currentIcao !== oPlane.locationIcao
  const rPilot = staff.find(s => s.id === rStaff)
  const rMismatch = rPilot?.currentIcao && rOrigin && rPilot.currentIcao !== rOrigin
  const createOrder = async () => {
    if (!oStaff || !oAircraft || !oDest) { setMsg('Pick a pilot, an aircraft, and a destination.'); return }
    setBusy(true); setMsg(null)
    try { await api.createOrder(oStaff, oAircraft, oDest, oMarkup); setOStaff(''); setOAircraft(''); setODest(''); setOMarkup(1000); await load(); onChanged(); setMsg("Standing order set — it flies while you're away.") }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const reprice = async (o: StandingOrder, milli: number) => {
    setBusy(true); setMsg(null)
    try { await api.setOrderPrice(o.id, milli); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const cancel = async (o: StandingOrder) => {
    setBusy(true); setMsg(null)
    try { await api.cancelOrder(o.id); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const process = async () => {
    setBusy(true); setMsg(null)
    try {
      const d: ReconcileResult = await api.reconcile()
      await load(); onChanged()
      const base = d.trips > 0 || d.wagesCents > 0 || d.rentCents > 0
        ? `Booked ${d.trips} trip${d.trips === 1 ? '' : 's'}: ${money(d.grossIncomeCents)} gross − ${money(d.feesCents)} fees − ${money(d.fuelCents)} fuel − ${money(d.wagesCents)} wages − ${money(d.rentCents)} rent = ${money(d.netCents)} net.`
        : 'Up to date — nothing new.'
      const inc = d.incidents > 0 ? ` ${d.incidents} trip${d.incidents === 1 ? '' : 's'} diverted — a sharper crew loses fewer.` : ''
      const empty = d.emptyLegs > 0 ? ` ${d.emptyLegs} leg${d.emptyLegs === 1 ? '' : 's'} flew empty — a lower markup keeps clients shipping.` : ''
      const duty = d.dutyMaxed.length > 0 ? ` ${d.dutyMaxed.join(', ')} hit the crew duty limit — hire another pilot to fly ${d.dutyMaxed.length === 1 ? 'it' : 'them'} harder.` : ''
      const warn = d.grounded.length > 0
        ? ` · Grounded, not flying: ${d.grounded.join('; ')} — service them in the Hangar.`
        : ''
      const owed = d.loanWarnings.length > 0 ? ` · ⚠ Can't cover loans (${d.loanWarnings.join('; ')}) — earn or pay down before they default.` : ''
      const def = d.defaults.length > 0 ? ` · ✖ Defaulted: ${d.defaults.join('; ')}.` : ''
      const cert = d.certLapsed.length > 0 ? ` · ⚠ Held (certificate lapsed): ${d.certLapsed.join('; ')} — renew in the Airline tab to resume.` : ''
      const wx = d.weatheredOut > 0 ? ` · ${d.weatheredOut} trip${d.weatheredOut === 1 ? '' : 's'} weathered out at the origin.` : ''
      const cx = d.certExpiring.length > 0 ? ` · ⚠ Renew soon: ${d.certExpiring.join('; ')} — renew in the Airline tab before it lapses.` : ''
      const rtn = d.rentalsAutoReturned.length > 0 ? ` · Rental${d.rentalsAutoReturned.length === 1 ? '' : 's'} returned at term end: ${d.rentalsAutoReturned.join(', ')}.` : ''
      const rx = d.rentalsExpiring.length > 0 ? ` · ⚠ Rental ending: ${d.rentalsExpiring.join('; ')} — return it or it auto-returns.` : ''
      // Phase 11a — the airline's own reputation moved by how well your crews flew (never a silent surprise, Law 4).
      const airep = d.operatingRepDeltaMilli !== 0
        ? ` · Airline reputation ${d.operatingRepDeltaMilli > 0 ? 'rose' : 'slipped'} ${(Math.abs(d.operatingRepDeltaMilli) / 1000).toFixed(1)} — ${d.operatingRepDeltaMilli > 0 ? 'your crews are flying well' : 'greener crews are dragging your name'}.`
        : ''
      const svc = d.repairCents > 0 ? ` · Managers serviced the fleet: ${money(d.repairCents)}.` : ''
      setMsg(base + inc + empty + duty + warn + owed + def + cert + wx + cx + rtn + rx + airep + svc)
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  // What the autonomous operation has flown but not yet banked — so "Process now" isn't a mystery button.
  const pendingTrips = orders.reduce((s, o) => s + (o.pendingTrips || 0), 0)
    + (routes?.routes.reduce((s, r) => s + (r.pendingTrips || 0), 0) ?? 0)
  const pendingIncome = orders.reduce((s, o) => s + (o.pendingIncomeCents || 0), 0)
    + (routes?.routes.reduce((s, r) => s + (r.pendingIncomeCents || 0), 0) ?? 0)

  return (
    <div className="grid">
      {(staff.length > 0 || orders.length > 0 || (routes?.routes.length ?? 0) > 0 || dispatches.length > 0) && (
        <div className="hero-stats tab-summary">
          <HeroStat label="Pilots" value={String(pilots.length)} accent />
          <HeroStat label="Managers" value={String(managers.length)} />
          <HeroStat label="Standing orders" value={String(orders.length)} />
          <HeroStat label="Routes" value={String(routes?.routes.length ?? 0)} />
          <HeroStat label="Dispatches" value={String(dispatches.length)} tone={dispatches.some(d => d.ready) ? 'pos' : undefined} />
        </div>
      )}
      <section className="card">
        <div className="row-head"><h2>Standing orders</h2>
          <span className="ops-process">
            {pendingTrips > 0 && <span className="pending-note">{pendingTrips} trip{pendingTrips === 1 ? '' : 's'} ready · ~{money(pendingIncome)}</span>}
            <button className="primary" disabled={busy} onClick={process}>Process now</button>
          </span>
        </div>
                {orders.length === 0
          ? <div className="empty">No standing orders. Set one below to earn while you're away.</div>
          : (
            <div className="tbl-wrap"><table className="tbl">
              <thead><tr><th>Pilot</th><th>Aircraft</th><th>Route</th><th className="r">Per trip</th><th className="r">Price</th><th className="r">Cycle</th><th className="r">Ready</th><th></th></tr></thead>
              <tbody>
                {orders.map(o => (
                  <tr key={o.id}>
                    <td>{o.staffName}</td>
                    <td>{o.aircraftName || o.tail}{o.aircraftName && <span className="muted loc"> · {o.tail}</span>}</td>
                    <td><b>{o.origin}</b> ↔ <b>{o.dest}</b> <span className="muted">· {Math.round(o.distanceNm)} nm</span></td>
                    <td className="r num pos">{money(o.rewardPerTripCents)}{o.priceMultiplierMilli > 1000 && <span className="fair-ref"> vs {money(o.fairRewardPerTripCents)}</span>}</td>
                    <td className="r">
                      <select className="markup-sel" value={o.priceMultiplierMilli} disabled={busy} onChange={e => reprice(o, Number(e.target.value))} title="Re-price this line — applies to future trips only">
                        {MARKUP_OPTS.map(m => <option key={m} value={m}>{markupLabel(m)}</option>)}
                      </select>
                      <span className={`fill-hint${o.fillPct < 100 ? ' warn' : ''}`}>{o.fillPct}% fill</span>
                    </td>
                    <td className="r num">{o.roundTripHours.toFixed(1)} h</td>
                    <td className="r">{o.pendingTrips > 0 ? <span className="pending-ready" title={`${o.pendingTrips} round trip${o.pendingTrips === 1 ? '' : 's'} flown since your last "Process now" — bank them for ~${money(o.pendingIncomeCents)}`}>{o.pendingTrips} · ~{money(o.pendingIncomeCents)}</span> : <span className="muted">—</span>}</td>
                    <td className="r"><button className="primary small" disabled={busy} onClick={() => cancel(o)}>Stop</button></td>
                  </tr>
                ))}
              </tbody>
            </table></div>
          )}
        {pilots.length > 0 && fleet.length > 0 && dests.length > 0 && (
          <>
          <div className="order-form">
            <select value={oStaff} onChange={e => setOStaff(e.target.value)}><option value="">Pilot…</option>{pilots.map(s => <option key={s.id} value={s.id} disabled={s.flying}>{s.name}{s.currentIcao ? ` · ${s.currentIcao}` : ''}{s.flying ? ' · busy' : ''}</option>)}</select>
            <select value={oAircraft} onChange={e => setOAircraft(e.target.value)}><option value="">Aircraft…</option>{fleet.map(f => <option key={f.id} value={f.id}>{f.name} · {f.tail} — {f.locationIcao}</option>)}</select>
            <select value={oDest} onChange={e => setODest(e.target.value)}><option value="">Destination…</option>{dests.map(d => <option key={d.icao} value={d.icao}>{d.icao} · {d.name}</option>)}</select>
            <select value={oMarkup} onChange={e => setOMarkup(Number(e.target.value))} title="Demand a premium over the fair rate — more per filled trip, but the client ships fewer">{MARKUP_OPTS.map(m => <option key={m} value={m}>{markupLabel(m)} · {fillFor(m)}% fill</option>)}</select>
            <button className="primary" disabled={busy} onClick={createOrder}>Set order</button>
          </div>
          {oMismatch && <div className="hint warn-text">{oPilot!.name} is at {oPilot!.currentIcao}; this aircraft is at {oPlane!.locationIcao}. Reposition the pilot there first (below), or pick one already there.</div>}
          </>
        )}
      </section>

      {dispatches.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Crew dispatches</h2><span className="hint">One-off jobs your crews are flying — they finish and pay automatically</span></div>
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Crew</th><th>Aircraft</th><th>Leg</th><th>Status</th><th className="r">Reward</th><th className="r"></th></tr></thead>
            <tbody>{dispatches.map(d => (
              <tr key={d.id}>
                <td>{d.crewName}</td>
                <td>{d.aircraftName || d.tail}{d.aircraftName && <span className="muted loc"> · {d.tail}</span>}</td>
                <td className="loc">{d.origin} → {d.dest} <span className="muted num">· {Math.round(d.distanceNm)} nm</span></td>
                <td>{d.ready ? <span className="pos">landed · banking…</span> : <span className="muted">{dueText(d.readyAt)}</span>}</td>
                <td className="r num pos">{money(d.rewardCents)}</td>
                <td className="r"><button className="linky" disabled={busy} title="Pull this crew leg out of the air early — only needed to abort; completed legs bank on their own" onClick={() => cancelDispatch(d.id)}>{d.ready ? '—' : 'Recall'}</button></td>
              </tr>
            ))}</tbody>
          </table></div>
        </section>
      )}

      <section className="card">
        <h2>Your crew</h2>
        {pilots.length === 0 ? <div className="empty">No pilots hired yet.</div> : (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Name</th><th>Based</th><th className="r">Skill</th><th className="r">Wage / day</th><th className="r"></th></tr></thead>
            <tbody>{pilots.map(s => (
              <tr key={s.id}>
                <td>{s.name}</td>
                <td className="loc">{s.flying ? <span className="muted">flying a line</span> : (s.currentIcao ?? '—')}</td>
                <td className="r num">{Math.round(s.skillMilli / 1000)}%</td>
                <td className="r num neg">{money(s.wagePerDayCents)}</td>
                <td className="r">
                  {s.flying
                    ? <span className="muted hint">busy</span>
                    : <>
                        <button className="linky" disabled={busy} onClick={() => relocateCrew(s)}>Reposition</button>
                        <button className="linky" disabled={busy} onClick={() => dismiss(s)}>Let go</button>
                      </>}
                </td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
        <h3 className="sub-h">Hire a pilot</h3>
        <div className="jobs">
          {candidates.map(c => (
            <div className="card job" key={c.seed}>
              <div className="leg"><b>{c.name}</b></div>
              <div className="job-meta">
                <Meta label="Skill" value={`${Math.round(c.skillMilli / 1000)}%`} />
                <Meta label="Wage/day" value={money(c.wagePerDayCents)} />
              </div>
              <div className="job-foot"><span /><button className="primary" disabled={busy} onClick={() => hire(c)}>Hire</button></div>
            </div>
          ))}
        </div>
      </section>

      <section className="card">
        <div className="row-head"><h2>Base managers</h2><span className="hint">A manager keeps the owned fleet at their base serviced — no more grounded tails while you're away</span></div>
        {managers.length > 0 && (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Name</th><th>Base</th><th className="r">Wage / day</th><th className="r"></th></tr></thead>
            <tbody>{managers.map(m => (
              <tr key={m.id}>
                <td>{m.name}</td>
                <td className="loc">{m.currentIcao}</td>
                <td className="r num neg">{money(m.wagePerDayCents)}</td>
                <td className="r"><button className="linky" disabled={busy} onClick={() => dismiss(m)}>Let go</button></td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
        {(routes?.bases.length ?? 0) === 0
          ? <div className="hint muted">Open a base first (Bases tab) — a manager runs one of your fields.</div>
          : manageableBases.length === 0
            ? <div className="hint muted">Every base has a manager.</div>
            : <div className="order-form">
                <select value={mgrBase} onChange={e => setMgrBase(e.target.value)}>
                  <option value="">Base…</option>
                  {manageableBases.map(b => <option key={b.icao} value={b.icao}>{b.icao} · {b.name}</option>)}
                </select>
                <button className="primary" disabled={busy || !mgrBase} onClick={() => mgrBase && hireManager(mgrBase)}>Hire manager</button>
              </div>}
      </section>

      <section className="card">
        <div className="row-head"><h2>Routes</h2><span className="hint">Base-to-base lines — fee-free, earning while you fly</span></div>
        {routes && routes.routes.length > 0 && (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Route</th><th>Leg</th><th className="r">Reward/trip</th><th className="r">Cycle</th><th className="r">Price</th><th className="r">Ready</th><th></th></tr></thead>
            <tbody>{routes.routes.map(r => (
              <tr key={r.id}>
                <td>{r.name} <span className="muted">· {r.seatCapacity != null ? `scheduled · ${r.seatCapacity} seats · ${Math.round((r.loadFactorMilli ?? 0) / 10)}% load` : r.mission}</span>
                  <div className="route-crew">{r.crewName} · <span className="num">{r.aircraftTail}</span></div>
                </td>
                <td><span className="loc">{r.origin}</span> → <span className="loc">{r.dest}</span> <span className="muted">· {Math.round(r.distanceNm)} nm</span></td>
                <td className="r num pos">{money(r.rewardPerTripCents)}{r.priceMultiplierMilli > 1000 && <span className="fair-ref"> vs {money(r.fairRewardPerTripCents)}</span>}</td>
                <td className="r num">{r.roundTripHours.toFixed(1)} h</td>
                <td className="r">
                  {r.seatCapacity != null
                    ? <span className="muted">fixed</span>
                    : <>
                        <select className="markup-sel" value={r.priceMultiplierMilli} disabled={busy} onChange={e => repriceRoute(r.id, Number(e.target.value))} title="Re-price this route — applies to future trips only">
                          {MARKUP_OPTS.map(m => <option key={m} value={m}>{markupLabel(m)}</option>)}
                        </select>
                        <span className={`fill-hint${r.fillPct < 100 ? ' warn' : ''}`}>{r.fillPct}% fill</span>
                      </>}
                </td>
                <td className="r">{r.pendingTrips > 0 ? <span className="pending-ready" title={`${r.pendingTrips} round trip${r.pendingTrips === 1 ? '' : 's'} flown since your last "Process now" — bank them for ~${money(r.pendingIncomeCents)}`}>{r.pendingTrips} · ~{money(r.pendingIncomeCents)}</span> : <span className="muted">—</span>}</td>
                <td className="r"><button disabled={busy} onClick={() => cancelRoute(r.id)}>Cancel</button></td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
        <h3 className="sub-h">Open a route</h3>
        {!routes || routes.bases.length < 2
          ? <div className="hint">You need at least two bases (open more on the Bases tab), plus an available aircraft and a hired pilot.</div>
          : (
            <div className="order-form">
              <input className="qty" style={{ width: 110 }} placeholder="Name" value={rName} onChange={e => setRName(e.target.value)} />
              <select value={rOrigin} onChange={e => setROrigin(e.target.value)}><option value="">From base…</option>{routes.bases.map(b => <option key={b.icao} value={b.icao}>{b.icao} · {b.name}</option>)}</select>
              <select value={rDest} onChange={e => setRDest(e.target.value)}><option value="">To base…</option>{routes.bases.map(b => <option key={b.icao} value={b.icao}>{b.icao} · {b.name}</option>)}</select>
              <select value={rStaff} onChange={e => setRStaff(e.target.value)}><option value="">Pilot…</option>{pilots.map(s => <option key={s.id} value={s.id} disabled={s.flying}>{s.name}{s.currentIcao ? ` · ${s.currentIcao}` : ''}{s.flying ? ' · busy' : ''}</option>)}</select>
              <select value={rAircraft} onChange={e => setRAircraft(e.target.value)}><option value="">Aircraft…</option>{fleet.map(f => <option key={f.id} value={f.id}>{f.name} · {f.tail} — {f.locationIcao}</option>)}</select>
              {rScheduled
                ? <span className="hint" style={{ alignSelf: 'center' }}>Scheduled: seats × load × yield, frozen at creation — your name fills the seats.</span>
                : <>
                    <select value={rMission} onChange={e => setRMission(e.target.value)}>{routes.missions.map(m => <option key={m} value={m}>{m}</option>)}</select>
                    <select value={rMarkup} onChange={e => setRMarkup(Number(e.target.value))} title="Demand a premium over the fair rate — more per filled trip, but the client ships fewer">{MARKUP_OPTS.map(m => <option key={m} value={m}>{markupLabel(m)} · {fillFor(m)}% fill</option>)}</select>
                  </>}
              {routes.hasAoc &&
                <label className="sched-toggle" title="Requires a valid Air Operator Certificate">
                  <input type="checkbox" checked={rScheduled} onChange={e => setRScheduled(e.target.checked)} /> Scheduled service
                </label>}
              <button className="primary" disabled={busy} onClick={createRoute}>{rScheduled ? 'Open scheduled service' : 'Open route'}</button>
            </div>
          )}
          {rMismatch && <div className="hint warn-text">{rPilot!.name} is at {rPilot!.currentIcao}; this route flies out of {rOrigin}. Reposition the pilot there first (in Your crew), or pick one already there.</div>}
      </section>
    </div>
  )
}

// ─── Self-rendered map (Phase 6b) ────────────────────────────────────────────

interface MapPoint { lat: number; lon: number; label?: string; kind?: 'home' | 'base' | 'field' }

// An original map: airports projected (equirectangular, longitude corrected for latitude) and fitted to
// the frame, on a graphite grid. Drawn from the public-domain coordinates we already ship — no tiles.
function MapView({ points }: { points: MapPoint[] }) {
  const W = 640, H = 300, pad = 30
  if (points.length === 0) return <div className="empty" style={{ padding: 20 }}>No locations to map yet.</div>

  const cLat = points.reduce((s, p) => s + p.lat, 0) / points.length
  const kx = Math.cos((cLat * Math.PI) / 180) // longitude compresses toward the poles
  const raw = points.map(p => ({ ...p, rx: p.lon * kx, ry: -p.lat }))
  const xs = raw.map(r => r.rx), ys = raw.map(r => r.ry)
  const cx = (Math.min(...xs) + Math.max(...xs)) / 2, cy = (Math.min(...ys) + Math.max(...ys)) / 2
  const spanx = Math.max(Math.max(...xs) - Math.min(...xs), 0.6), spany = Math.max(Math.max(...ys) - Math.min(...ys), 0.6)
  const minx = cx - spanx * 0.62, maxx = cx + spanx * 0.62, miny = cy - spany * 0.62, maxy = cy + spany * 0.62
  const gw = maxx - minx, gh = maxy - miny
  const scale = Math.min((W - 2 * pad) / gw, (H - 2 * pad) / gh)
  const ox = (W - gw * scale) / 2, oy = (H - gh * scale) / 2
  const at = (r: { rx: number; ry: number }) => [ox + (r.rx - minx) * scale, oy + (r.ry - miny) * scale] as const

  return (
    <div className="mapview">
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="xMidYMid meet" role="img" aria-label="Network map">
        <defs>
          <pattern id="mgrid" width="28" height="28" patternUnits="userSpaceOnUse">
            <path d="M28 0H0V28" fill="none" stroke="var(--line)" strokeWidth="1" opacity=".55" />
          </pattern>
        </defs>
        <rect width={W} height={H} fill="var(--panel-2)" />
        <rect width={W} height={H} fill="url(#mgrid)" />
        {raw.map((r, i) => {
          const [x, y] = at(r)
          const base = r.kind === 'home' || r.kind === 'base'
          return (
            <g key={i}>
              {base && <circle cx={x} cy={y} r={12} fill="var(--accent)" opacity=".15" />}
              <circle cx={x} cy={y} r={base ? 4.5 : 3} fill={base ? 'var(--accent)' : 'var(--muted)'} />
              {r.label && <text x={x + 8} y={y + 3.5} fontSize="10.5" fontFamily="var(--mono)" fill={base ? 'var(--ink)' : 'var(--faint)'}>{r.label}</text>}
            </g>
          )
        })}
      </svg>
    </div>
  )
}

// A real satellite map. Esri World Imagery — global aerial/satellite tiles, no API key, no backend, free to
// use with attribution. Markers are vector circles in the Sector palette. Offline (no tiles) we fall back to
// the self-rendered vector map above, so the app never depends on the network to show your network.
function SatelliteMap({ points }: { points: MapPoint[] }) {
  const host = useRef<HTMLDivElement>(null)
  const online = typeof navigator === 'undefined' ? true : navigator.onLine
  // A stable signature so the map rebuilds only when the plotted points actually change, not every render.
  const sig = points.map(p => `${p.lat.toFixed(4)},${p.lon.toFixed(4)},${p.kind ?? ''},${p.label ?? ''}`).join('|')

  useEffect(() => {
    if (!host.current || points.length === 0 || !online) return
    const map = L.map(host.current, { attributionControl: true, zoomControl: true, worldCopyJump: true })
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
      attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics',
      maxZoom: 18,
    }).addTo(map)
    const markers = points.map(p => {
      const base = p.kind === 'home' || p.kind === 'base'
      const m = L.circleMarker([p.lat, p.lon], {
        radius: base ? 7 : 5, weight: 2,
        color: base ? '#6d84ff' : '#e9eef5',
        fillColor: base ? '#6d84ff' : '#8a97a7',
        fillOpacity: base ? 0.85 : 0.7,
      }).addTo(map)
      if (p.label) m.bindTooltip(p.label, { permanent: base, direction: 'right', className: 'sat-tip', offset: [6, 0] })
      return m
    })
    map.fitBounds(L.featureGroup(markers).getBounds().pad(0.4), { maxZoom: 9 })
    const t = setTimeout(() => map.invalidateSize(), 60) // WebView2 flex layout can settle a beat late
    return () => { clearTimeout(t); map.remove() }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, online])

  if (points.length === 0) return <div className="empty" style={{ padding: 20 }}>No locations to map yet.</div>
  if (!online) return <MapView points={points} />
  return <div className="satmap" ref={host} role="img" aria-label="Satellite network map" />
}

// ─── Bases ───────────────────────────────────────────────────────────────────

function Bases({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [bases, setBases] = useState<BaseView[]>([])
  const [offers, setOffers] = useState<BaseOffer[]>([])
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()

  const load = useCallback(async () => {
    try { setBases(await api.bases()); setOffers(await api.baseCandidates()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const open = async (o: BaseOffer) => {
    setBusy(true); setMsg(null)
    try { await api.openBase(o.icao); await load(); onChanged(); setMsg(`Opened a base at ${o.icao} · ${o.name}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const upgradeShop = async (b: BaseView) => {
    setBusy(true); setMsg(null)
    try { const r = await api.upgradeShop(b.id); await load(); onChanged(); setMsg(`${b.icao} maintenance shop is now level ${r.maintenanceLevel} — cheaper servicing for tails based there.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const upgradeFuelFarm = async (b: BaseView) => {
    setBusy(true); setMsg(null)
    try { const r = await api.upgradeFuelFarm(b.id); await load(); onChanged(); setMsg(`${b.icao} fuel farm is now level ${r.fuelFarmLevel} — cheaper fuel on legs departing there.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const upgradeHub = async (b: BaseView) => {
    setBusy(true); setMsg(null)
    try { const r = await api.upgradeHub(b.id); await load(); onChanged(); setMsg(`${b.icao} is now a level ${r.hubLevel} hub — your name draws demand harder on jobs and routes here.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const mapPoints: MapPoint[] = [
    ...bases.map((b): MapPoint => ({ lat: b.latitude, lon: b.longitude, label: b.icao, kind: b.isHome ? 'home' : 'base' })),
    ...offers.map((o): MapPoint => ({ lat: o.latitude, lon: o.longitude, label: o.icao, kind: 'field' })),
  ].filter(p => p.lat !== 0 || p.lon !== 0)

  return (
    <div className="grid">
      {bases.length > 0 && (
        <div className="hero-stats tab-summary">
          <HeroStat label="Bases" value={String(bases.length)} accent />
          <HeroStat label="Hubs" value={String(bases.filter(b => b.hubLevel > 0).length)} />
          <HeroStat label="Maintenance shops" value={String(bases.filter(b => b.maintenanceLevel > 0).length)} />
          <HeroStat label="Fuel farms" value={String(bases.filter(b => b.fuelFarmLevel > 0).length)} />
          <HeroStat label="Daily rent" value={money(bases.reduce((s, b) => s + b.rentPerDayCents, 0))} tone="neg" />
        </div>
      )}
      <section className="card">
        <div className="row-head"><h2>Your network</h2><span className="hint">satellite · {bases.length} base{bases.length === 1 ? '' : 's'}</span></div>
        <SatelliteMap points={mapPoints} />
      </section>
      <section className="card">
        <h2>Your bases</h2>
        {bases.length === 0 ? <div className="empty">No bases.</div> : (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Airport</th><th>Name</th><th className="r">Rent / day</th><th>Maintenance shop</th><th>Fuel farm</th><th>Hub</th></tr></thead>
            <tbody>{bases.map(b => (
              <tr key={b.id}>
                <td><span className="loc">{b.icao}</span>{b.isHome && <span className="tag" style={{ marginLeft: 8 }}>home</span>}</td>
                <td>{b.name}</td>
                <td className="r num muted">{b.rentPerDayCents ? money(b.rentPerDayCents) : 'free'}</td>
                <td className="shop-cell">
                  {b.maintenanceLevel > 0
                    ? <span className="shop-lvl">L{b.maintenanceLevel} <span className="muted">· {Math.round(b.maintenanceDiscountPct * 100)}% off servicing</span></span>
                    : <span className="muted">none</span>}
                  {b.nextShopUpgradeCents > 0 &&
                    <button className="small" disabled={busy} onClick={() => upgradeShop(b)}>
                      {b.maintenanceLevel > 0 ? 'Upgrade' : 'Build'} · {money(b.nextShopUpgradeCents)}
                    </button>}
                </td>
                <td className="shop-cell">
                  {b.fuelFarmLevel > 0
                    ? <span className="shop-lvl">L{b.fuelFarmLevel} <span className="muted">· {Math.round(b.fuelDiscountPct * 100)}% off fuel</span></span>
                    : <span className="muted">none</span>}
                  {b.nextFuelFarmUpgradeCents > 0 &&
                    <button className="small" disabled={busy} onClick={() => upgradeFuelFarm(b)}>
                      {b.fuelFarmLevel > 0 ? 'Upgrade' : 'Build'} · {money(b.nextFuelFarmUpgradeCents)}
                    </button>}
                </td>
                <td className="shop-cell">
                  {b.hubLevel > 0
                    ? <span className="shop-lvl">L{b.hubLevel} <span className="muted">· ×{b.hubDemandAmplification.toFixed(1)} demand lift</span></span>
                    : <span className="muted">none</span>}
                  {b.nextHubUpgradeCents > 0 &&
                    <button className="small" disabled={busy} onClick={() => upgradeHub(b)}>
                      {b.hubLevel > 0 ? 'Upgrade' : 'Promote'} · {money(b.nextHubUpgradeCents)}
                    </button>}
                </td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </section>

      <section className="card">
        <div className="row-head"><h2>Open a base</h2><span className="hint">Land fee-free at your own bases</span></div>
                {offers.length === 0 ? <div className="empty">No nearby airports to base at.</div> : (
          <div className="jobs">
            {offers.map(o => {
              const afford = state.cashCents >= o.openCents
              return (
                <div className="card job" key={o.icao}>
                  <div className="job-top"><div className="leg"><b>{o.icao}</b></div><div className="tag">{spaced(o.kind).replace(' Airport', '')}</div></div>
                  <div className="dest-name">{o.name}</div>
                  <div className="job-meta">
                    <Meta label="Distance" value={`${Math.round(o.distanceNm)} nm`} />
                    <Meta label="Rent/day" value={money(o.rentPerDayCents)} />
                  </div>
                  <div className="price num">{money(o.openCents)}</div>
                  <div className="job-foot">
                    <span className="hint">{afford ? '' : 'over budget'}</span>
                    <button className="primary" disabled={busy || !afford} onClick={() => open(o)}>Open base</button>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>
    </div>
  )
}

// ─── Trade ───────────────────────────────────────────────────────────────────

function Trade({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [market, setMarket] = useState<MarketQuote[]>([])
  const [inv, setInv] = useState<Inventory[]>([])
  const [qty, setQty] = useState<Record<string, string>>({}) // raw input strings, so a field can be cleared/retyped freely; clamped only at buy/sell
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()

  const load = useCallback(async () => {
    try { setMarket(await api.tradeMarket()); setInv(await api.inventory()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const q = (key: string) => Math.max(1, Math.floor(Number(qty[key]) || 1))
  const setQ = (key: string, v: string) => setQty(s => ({ ...s, [key]: v }))

  const buy = async (good: string) => {
    setBusy(true); setMsg(null)
    try { await api.buyGood(good, q('buy-' + good)); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const sell = async (good: string, lotId: string, max: number) => {
    setBusy(true); setMsg(null)
    try {
      const r = await api.sellGood(good, Math.min(q('sell-' + lotId), max))
      await load(); onChanged()
      const pnl = r.pnlCents >= 0 ? `+${money(r.pnlCents)}` : money(r.pnlCents)
      setMsg(`Sold ${r.quantity} — proceeds ${money(r.proceedsCents)}, P&L ${pnl}.`)
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const discard = async (good: string) => {
    setBusy(true); setMsg(null)
    try { await api.discardGood(good); await load(); onChanged(); setMsg('Discarded the spoiled goods — the hold is free again.') }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      {(() => {
        const margins = market.map(m => m.bestSellMarginCents).filter((v): v is number => typeof v === 'number' && Number.isFinite(v))
        const bestMargin = margins.length ? Math.max(...margins) : 0
        const perishing = inv.filter(v => v.spoiled || (v.freshDaysLeft != null && v.freshDaysLeft <= 2)).length
        return (
          <div className="hero-stats tab-summary">
            <HeroStat label="Cash" value={money(state.cashCents)} accent />
            <HeroStat label="Holdings value" value={money(inv.reduce((s, v) => s + v.marketSellCents * v.quantity, 0))} />
            {bestMargin > 0 && <HeroStat label="Best margin out" value={`${money(bestMargin)}/u`} tone="pos" />}
            {perishing > 0 && <HeroStat label="Perishing" value={String(perishing)} tone="neg" />}
          </div>
        )
      })()}
      <section className="card">
        <div className="row-head"><h2>Market · <span className="loc">{state.currentIcao}</span></h2><span className="hint">Buy low here, fly it, sell high there — best sell shown</span></div>
                <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th>Commodity</th><th className="r">Buy</th><th className="r">Sell</th><th>Best sell elsewhere</th><th className="r">Unit wt</th><th className="r">Qty</th><th></th></tr></thead>
            <tbody>{[...market].sort((a, b) => b.bestSellMarginCents - a.bestSellMarginCents).map(m => (
              <tr key={m.good}>
                <td>
                  {m.name}
                  {m.region === 'export' ? <span className="region-tag exp">produced here</span> : m.region === 'demand' ? <span className="region-tag dem">in demand</span> : null}
                  {m.pressurePct >= 1 ? <span className="pressure-tag up" title="Your buying has bid this market up. It drifts back to normal once you stop.">you bid +{m.pressurePct}%</span>
                    : m.pressurePct <= -1 ? <span className="pressure-tag down" title="Your selling has flooded this market. It drifts back to normal once you stop.">you softened −{Math.abs(m.pressurePct)}%</span> : null}
                  {m.weatherPct >= 1 ? <span className="weather-tag" title="Foul weather here has lifted local prices — sell into it dear, but the landing is harder.">weather +{m.weatherPct}%</span> : null}
                  {m.shelfLifeDays != null ? <span className="region-tag perish" title={`Perishable — spoils ${m.shelfLifeDays} days after you buy it. Sell it before then or it's a total loss.`}>perishable {m.shelfLifeDays}d</span> : null}
                </td>
                <td className="r num">{money(m.buyCents)}</td>
                <td className="r num muted">{money(m.sellCents)}</td>
                <td>
                  {m.bestSellIcao
                    ? <span className="best-sell" title={`Buy here at ${money(m.buyCents)}, fly ${Math.round(m.bestSellDistanceNm)} nm to ${m.bestSellIcao} and sell at ${money(m.bestSellCents)}`}>
                        → <span className="loc">{m.bestSellIcao}</span>{' '}
                        <span className={`num ${m.bestSellMarginCents > 0 ? 'pos' : 'muted'}`}>{m.bestSellMarginCents > 0 ? `+${money(m.bestSellMarginCents)}` : money(m.bestSellMarginCents)}/u</span>
                        <span className="muted"> · {Math.round(m.bestSellDistanceNm)} nm</span>
                      </span>
                    : <span className="muted">—</span>}
                </td>
                <td className="r muted">{m.unitWeightLbs} lb</td>
                <td className="r"><input className="qty" type="number" min={1} value={qty['buy-' + m.good] ?? '1'} onChange={e => setQ('buy-' + m.good, e.target.value)} /></td>
                <td className="r"><button disabled={busy} onClick={() => buy(m.good)}>Buy</button></td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      </section>

      <section className="card">
        <h2>Holdings</h2>
        {inv.length === 0 ? <div className="empty">No goods held. Buy something on the market to start trading.</div> : (
          <div className="tbl-wrap">
            <table className="tbl">
              <thead><tr><th>Commodity</th><th className="r">Qty</th><th className="r">Avg cost</th><th className="r">Sell here</th><th className="r">Unrealised</th><th>At</th><th className="r">Qty</th><th></th></tr></thead>
              <tbody>{inv.map(v => {
                const here = v.locationIcao === state.currentIcao
                const freshTag = v.spoiled
                  ? <span className="region-tag spoiled" title="Spoiled — worthless. Discard it to free the hold.">spoiled</span>
                  : v.freshDaysLeft != null
                    ? <span className={`region-tag ${v.freshDaysLeft <= 2 ? 'perish-warn' : 'perish'}`} title={`Spoils in about ${Math.max(0, Math.ceil(v.freshDaysLeft))} day(s) — sell before then`}>fresh {Math.max(0, Math.ceil(v.freshDaysLeft))}d</span>
                    : null
                return (
                  <tr key={v.id} className={v.spoiled ? 'spoiled-row' : ''}>
                    <td>{v.name}{freshTag}</td>
                    <td className="r num">{v.quantity}</td>
                    <td className="r num muted">{money(v.unitCostCents)}</td>
                    <td className="r num">{v.spoiled ? <span className="neg">worthless</span> : money(v.marketSellCents)}</td>
                    <td className={`r num ${v.unrealizedPnlCents >= 0 ? 'pos' : 'neg'}`}>{money(v.unrealizedPnlCents)}</td>
                    <td><span className="loc">{v.locationIcao}</span></td>
                    <td className="r">{!v.spoiled && <input className="qty" type="number" min={1} max={v.quantity} value={qty['sell-' + v.id] ?? '1'} onChange={e => setQ('sell-' + v.id, e.target.value)} />}</td>
                    <td className="r">{v.spoiled
                      ? <button className="danger" disabled={busy || !here} title={here ? 'Throw away the spoiled goods' : `Fly to ${v.locationIcao} to discard`} onClick={() => discard(v.good)}>Discard</button>
                      : <button disabled={busy || !here} title={here ? '' : `Fly to ${v.locationIcao} to sell`} onClick={() => sell(v.good, v.id, v.quantity)}>Sell</button>}
                    </td>
                  </tr>
                )
              })}</tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  )
}

// ─── Finances (loans) ────────────────────────────────────────────────────────

// ─── Chart primitives (Phase 6) ──────────────────────────────────────────────

// A responsive SVG trend line — balance over time, landing-fpm trend. Fixed viewBox, scales uniformly.
function Trendline({ values, tone = 'accent' }: { values: number[]; tone?: string }) {
  if (values.length < 2) return <div className="chart-empty">Not enough data yet — fly a few legs.</div>
  const W = 600, H = 120, P = 12
  const min = Math.min(...values), max = Math.max(...values)
  const range = max - min || 1
  const x = (i: number) => P + (i / (values.length - 1)) * (W - 2 * P)
  const y = (v: number) => P + (1 - (v - min) / range) * (H - 2 * P)
  const line = values.map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ')
  const area = `${x(0).toFixed(1)},${H - P} ${line} ${x(values.length - 1).toFixed(1)},${H - P}`
  return (
    <svg className={`trend ${tone}`} viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Trend chart">
      <polygon className="trend-area" points={area} />
      <polyline className="trend-line" points={line} />
      <circle className="trend-dot" cx={x(values.length - 1)} cy={y(values[values.length - 1])} r="4.5" />
    </svg>
  )
}

// A labelled horizontal bar — P&L per category, net-worth composition.
function BarRow({ label, value, max, tone }: { label: string; value: number; max: number; tone: 'pos' | 'neg' | 'accent' }) {
  const pct = max > 0 ? Math.min(100, (Math.abs(value) / max) * 100) : 0
  return (
    <div className="barrow">
      <span className="barrow-label">{label}</span>
      <div className="barrow-track"><div className={`barrow-fill ${tone}`} style={{ width: `${Math.max(2, pct)}%` }} /></div>
      <span className={`barrow-val num ${tone === 'accent' ? '' : tone}`}>{money(value)}</span>
    </div>
  )
}

// ─── Finances period selector ─────────────────────────────────────────────────
const FIN_PERIODS: { key: number; label: string }[] = [
  { key: 7, label: '7d' }, { key: 30, label: '30d' }, { key: 90, label: '90d' },
  { key: 365, label: '1y' }, { key: 3650, label: 'All' },
]

// A diverging per-bucket cash-flow strip: income rises from the mid-line, expense falls below it.
// Hand-built (the kit's Trendline is single-series); coheres via the same tokens as BarRow.
function FlowColumns({ points }: { points: CashPoint[] }) {
  if (points.length === 0) return <div className="chart-empty">No movement in this window.</div>
  const mag = Math.max(1, ...points.map(p => Math.max(p.incomeCents, -p.expenseCents)))
  return (
    <div className="flowcols" role="img" aria-label="Cash flow by period">
      {points.map((p, i) => {
        const up = Math.max(0, (p.incomeCents / mag) * 100)
        const dn = Math.max(0, (-p.expenseCents / mag) * 100)
        const tip = `${when(p.at)} · in ${money(p.incomeCents)} · out ${money(p.expenseCents)}`
        return (
          <div className="flowcol" key={i} title={tip}>
            <div className="flow-up"><div className="flow-bar pos" style={{ height: `${up}%` }} /></div>
            <div className="flow-mid" />
            <div className="flow-dn"><div className="flow-bar neg" style={{ height: `${dn}%` }} /></div>
          </div>
        )
      })}
    </div>
  )
}

// The drill-down panel for a selected aircraft / pilot / base — the reusable selected-row → detail
// pattern (mirrors JobDetail). Shows the subject's split of the period P&L.
function AttributionDetail({ line, groupNet }: { line: AttributionLine; groupNet: number }) {
  const share = groupNet !== 0 ? Math.round((line.netCents / groupNet) * 100) : 0
  const mag = Math.max(1, line.incomeCents, -line.expenseCents)
  return (
    <div className="card jdetail fd-detail">
      <div className="fd-head">
        <div className="fd-kind metalabel">{line.kind}</div>
        <div className="fd-title">{line.label}</div>
        <div className="fd-sub muted">{line.sub} · {line.entries} {line.entries === 1 ? 'entry' : 'entries'}</div>
      </div>
      <div className="fd-net">
        <span className="metalabel">Net this period</span>
        <span className={`num ${line.netCents >= 0 ? 'pos' : 'neg'}`}>{money(line.netCents)}</span>
      </div>
      <div className="bars fd-bars">
        <BarRow label="Income" value={line.incomeCents} max={mag} tone="pos" />
        <BarRow label="Expense" value={line.expenseCents} max={mag} tone="neg" />
      </div>
      <div className="jd-pay">
        <div className="jd-payrow"><span className="muted">Share of group net</span><span className="num">{share}%</span></div>
        <div className="jd-payrow"><span className="muted">Avg / entry</span>
          <span className={`num ${line.netCents >= 0 ? 'pos' : 'neg'}`}>
            {money(Math.round(line.netCents / Math.max(1, line.entries)))}</span></div>
      </div>
    </div>
  )
}

// Per-subject P&L with a selectable list on the left and a detail panel on the right.
function AttributionPanel({ detail }: { detail: FinanceDetail }) {
  const groups: { key: string; label: string; lines: AttributionLine[] }[] = [
    { key: 'aircraft', label: 'By aircraft', lines: detail.aircraft },
    { key: 'staff', label: 'By pilot', lines: detail.staff },
    { key: 'bases', label: 'By base', lines: detail.bases },
  ].filter(g => g.lines.length > 0)
  const [tab, setTab] = useState(groups[0]?.key ?? 'aircraft')
  const [sel, setSel] = useState<string | null>(null)
  const active = groups.find(g => g.key === tab) ?? groups[0]

  if (groups.length === 0) return null
  const lines = active.lines
  const groupNet = lines.reduce((s, l) => s + l.netCents, 0)
  const mag = Math.max(1, ...lines.map(l => Math.abs(l.netCents)))
  const selected = lines.find(l => l.id === sel) ?? lines[0]

  return (
    <section className="card">
      <div className="row-head">
        <h2>Profitability</h2>
        <div className="seg">
          {groups.map(g => (
            <button key={g.key} className={`seg-btn ${g.key === active.key ? 'on' : ''}`}
              onClick={() => { setTab(g.key); setSel(null) }}>{g.label}</button>
          ))}
        </div>
      </div>
      <div className="attr-layout">
        <div className="attr-list">
          {lines.map(l => {
            const on = l.id === selected.id
            const pct = Math.max(3, (Math.abs(l.netCents) / mag) * 100)
            return (
              <button key={l.id} className={`attr-row ${on ? 'on' : ''}`} onClick={() => setSel(l.id)}>
                <span className="attr-name">{l.label}<span className="attr-sub muted"> · {l.sub}</span></span>
                <span className="attr-track">
                  <span className={`attr-fill ${l.netCents >= 0 ? 'pos' : 'neg'}`} style={{ width: `${pct}%` }} />
                </span>
                <span className={`num attr-val ${l.netCents >= 0 ? 'pos' : 'neg'}`}>{money(l.netCents)}</span>
              </button>
            )
          })}
        </div>
        <AttributionDetail line={selected} groupNet={groupNet} />
      </div>
    </section>
  )
}

function Finances({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [data, setData] = useState<Loans | null>(null)
  const [fin, setFin] = useState<FinancesData | null>(null)
  const [detail, setDetail] = useState<FinanceDetail | null>(null)
  const [stmt, setStmt] = useState<StatementRow[]>([])
  const [ins, setIns] = useState<Insurance | null>(null)
  const [days, setDays] = useState(30)
  const [amount, setAmount] = useState(50000) // dollars
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()

  // Loans + insurance don't depend on the period; balance sheet + P&L + statement do.
  const loadStatic = useCallback(async () => {
    try { setData(await api.loans()); setIns(await api.insurance()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  const loadPeriod = useCallback(async (d: number) => {
    try {
      setFin(await api.finances(d))
      setDetail(await api.financesDetail(d))
      setStmt(await api.statement(d))
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void loadStatic() }, [loadStatic])
  useEffect(() => { void loadPeriod(days) }, [days, loadPeriod])
  const reloadAll = useCallback(async () => { await loadStatic(); await loadPeriod(days) }, [loadStatic, loadPeriod, days])

  const insure = async (aircraftInstanceId: string) => {
    setBusy(true); setMsg(null)
    try { await api.insure(aircraftInstanceId); await reloadAll(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const cancelIns = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.cancelInsurance(id); await reloadAll(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const claim = async (id: string) => {
    setBusy(true); setMsg(null)
    try { const r = await api.claimInsurance(id); await reloadAll(); onChanged(); setMsg(`Claim paid — ${money(r.paidCents)}. Airframe written off.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const cents = Math.max(0, Math.round(amount * 100))
  const tier: LoanOffer | undefined = data?.offers.find(o => cents >= o.minPrincipalCents && cents <= o.maxPrincipalCents)

  const take = async () => {
    setBusy(true); setMsg(null)
    try { await api.takeLoan(cents); await reloadAll(); onChanged(); setMsg(`Borrowed ${money(cents)} — it's in your cash.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const payoff = async (l: Loan) => {
    setBusy(true); setMsg(null)
    try { const r = await api.payoffLoan(l.id); await reloadAll(); onChanged(); setMsg(`Loan cleared — paid ${money(r.paidCents)}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const exportCsv = () => {
    if (stmt.length === 0) return
    const head = ['Date', 'Category', 'Amount', 'Description', 'Aircraft', 'Pilot', 'Base']
    const esc = (v: string) => `"${v.replace(/"/g, '""')}"`
    const rows = stmt.map(r => [
      new Date(r.at).toISOString(), spaced(r.category), (r.amountCents / 100).toFixed(2),
      r.description ?? '', r.aircraft ?? '', r.staff ?? '', r.base ?? '',
    ].map(v => esc(String(v))).join(','))
    const csv = [head.map(esc).join(','), ...rows].join('\r\n')
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `callsign-statement-${days}d-${new Date().toISOString().slice(0, 10)}.csv`
    document.body.appendChild(a); a.click(); a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }

  const periodLabel = FIN_PERIODS.find(p => p.key === days)?.label ?? `${days}d`
  const cashSeries = detail ? detail.series.map(p => Math.round(p.cashCents / 100)) : []

  return (
    <div className="grid">
      {/* Headline: net worth + income/expense/net for the selected window, with the period selector. */}
      {fin && (
        <section className="card">
          <div className="row-head">
            <h2>Finances</h2>
            <div className="seg">
              {FIN_PERIODS.map(p => (
                <button key={p.key} className={`seg-btn ${p.key === days ? 'on' : ''}`}
                  onClick={() => setDays(p.key)}>{p.label}</button>
              ))}
            </div>
          </div>
          <div className="hero-stats fin-headline">
            <HeroStat label="Net worth" value={money(fin.netWorth.netWorthCents)} accent />
            <HeroStat label={`Income · ${periodLabel}`} value={money(fin.pnl.incomeCents)} />
            <HeroStat label={`Expenses · ${periodLabel}`} value={money(fin.pnl.expenseCents)} />
            <HeroStat label={`Net · ${periodLabel}`} value={money(fin.pnl.netCents)} />
          </div>
        </section>
      )}

      {/* Wealth over time + cash-flow rhythm. */}
      {detail && detail.series.length > 1 && (
        <section className="card">
          <h2>Cash over time</h2>
          <div className="trends">
            <div className="trend-cell">
              <div className="trend-head"><span className="metalabel">Cash balance</span><span className="num">{money(state.cashCents)}</span></div>
              <Trendline values={cashSeries} tone={cashSeries[cashSeries.length - 1] >= cashSeries[0] ? 'pos' : 'neg'} />
            </div>
            <div className="trend-cell">
              <div className="trend-head"><span className="metalabel">In vs out</span><span className={`num ${fin && fin.pnl.netCents >= 0 ? 'pos' : 'neg'}`}>{fin ? money(fin.pnl.netCents) : '—'}</span></div>
              <FlowColumns points={detail.series} />
            </div>
          </div>
        </section>
      )}

      {/* Per-aircraft / per-pilot / per-base profitability, with drill-down. */}
      {detail && <AttributionPanel detail={detail} />}

      {/* Net-worth composition. */}
      {fin && (
        <section className="card">
          <div className="row-head"><h2>Balance sheet</h2><span className={`num rep-score ${fin.netWorth.netWorthCents >= 0 ? 'pos' : 'neg'}`}>{money(fin.netWorth.netWorthCents)}</span></div>
          {(() => {
            const nw = fin.netWorth
            const rows: { label: string; value: number; tone: 'accent' | 'neg' }[] = [
              { label: 'Cash', value: nw.cashCents, tone: 'accent' },
              { label: 'Aircraft', value: nw.aircraftCents, tone: 'accent' },
              { label: 'Inventory', value: nw.inventoryCents, tone: 'accent' },
              { label: 'Loans', value: -nw.loansCents, tone: 'neg' },
            ]
            const max = Math.max(1, ...rows.map(r => Math.abs(r.value)))
            return <div className="bars">{rows.map(r => <BarRow key={r.label} label={r.label} value={r.value} max={max} tone={r.tone} />)}</div>
          })()}
        </section>
      )}

      {/* P&L by category. */}
      {fin && fin.pnl.lines.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Cash flow · by category</h2><span className={`num rep-score ${fin.pnl.netCents >= 0 ? 'pos' : 'neg'}`}>{money(fin.pnl.netCents)}</span></div>
          {(() => {
            const max = Math.max(1, ...fin.pnl.lines.map(l => Math.abs(l.netCents)))
            return <div className="bars">{fin.pnl.lines.map(l => <BarRow key={l.category} label={spaced(l.category)} value={l.netCents} max={max} tone={l.netCents >= 0 ? 'pos' : 'neg'} />)}</div>
          })()}
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Category</th><th className="r">In</th><th className="r">Out</th><th className="r">Net</th></tr></thead>
            <tbody>{fin.pnl.lines.map(l => (
              <tr key={l.category}>
                <td>{spaced(l.category)}</td>
                <td className="r num muted">{l.incomeCents ? money(l.incomeCents) : '—'}</td>
                <td className="r num muted">{l.expenseCents ? money(l.expenseCents) : '—'}</td>
                <td className={`r num ${l.netCents >= 0 ? 'pos' : 'neg'}`}>{money(l.netCents)}</td>
              </tr>
            ))}</tbody>
          </table></div>
        </section>
      )}

      {/* Loans — now with term / taken / due / repayment progress. */}
      <section className="card">
        <h2>Your loans</h2>
                {!data ? <div className="empty">Loading…</div>
          : data.loans.length === 0 ? <div className="empty">No loans outstanding. Borrow below to grow faster.</div>
            : (
              <div className="loan-list">
                {data.loans.map(l => {
                  const name = data.offers.find(o => o.tier === l.tier)?.name ?? `Tier ${l.tier}`
                  const repaid = l.principalCents > 0 ? Math.max(0, Math.min(1, 1 - l.outstandingCents / l.principalCents)) : 0
                  const due = new Date(new Date(l.takenAt).getTime() + l.termDays * 86400000)
                  const daysLeft = Math.ceil((due.getTime() - Date.now()) / 86400000)
                  return (
                    <div className="loan-row" key={l.id}>
                      <div className="loan-top">
                        <div>
                          <div className="loan-name">{name}</div>
                          <div className="loan-meta muted">
                            {money(l.principalCents)} borrowed · {(l.aprBps / 100).toFixed(1)}% APR · {l.termDays}-day term · taken {when(l.takenAt)}
                          </div>
                        </div>
                        <div className="loan-right">
                          <div className="loan-out num">{money(l.outstandingCents)}</div>
                          <div className="metalabel">outstanding</div>
                        </div>
                      </div>
                      <div className="loan-prog"><div className="loan-fill" style={{ width: `${repaid * 100}%` }} /></div>
                      <div className="loan-foot">
                        <span className="muted">{Math.round(repaid * 100)}% repaid · {daysLeft > 0 ? `${daysLeft} days left` : 'past term'}</span>
                        <button disabled={busy || state.cashCents < l.outstandingCents}
                          title={state.cashCents < l.outstandingCents ? 'Not enough cash to clear it' : ''}
                          onClick={() => payoff(l)}>Pay off {money(l.outstandingCents)}</button>
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
      </section>

      <section className="card">
        <div className="row-head">
          <h2>Borrow</h2>
          {data && <span className={`credit-badge g-${data.credit.grade}`} title={`Credit score ${data.credit.score}/100 — ${data.credit.aprDeltaBps === 0 ? 'terms at the listed rate' : data.credit.aprDeltaBps < 0 ? `${(-data.credit.aprDeltaBps / 100).toFixed(1)}% off every rate` : `+${(data.credit.aprDeltaBps / 100).toFixed(1)}% on every rate`}`}>Rating {data.credit.grade} · {data.credit.score}</span>}
        </div>
        <label className="pick">Amount ($)&nbsp;
          <input type="number" min={0} step={1000} value={amount} onChange={e => setAmount(Number(e.target.value))} />
        </label>
        <p className="muted" style={{ margin: '10px 0' }}>
          {tier
            ? <>Tier <b>{tier.name}</b> at <b>{(tier.effectiveAprBps / 100).toFixed(1)}%</b> APR{tier.effectiveAprBps !== tier.aprBps && <span> ({tier.effectiveAprBps < tier.aprBps ? 'discounted from' : 'up from'} {(tier.aprBps / 100).toFixed(1)}% by your rating)</span>}, repaid over 90 days. You have {money(state.cashCents)}.</>
            : 'That amount is outside the lending range.'}
        </p>
        <button className="primary" disabled={busy || !tier} onClick={take}>Borrow {money(cents)}</button>
      </section>

      <section className="card">
        <h2>Lending tiers</h2>
        {data && (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Tier</th><th className="r">From</th><th className="r">To</th><th className="r">Your APR</th></tr></thead>
            <tbody>{data.offers.map(o => (
              <tr key={o.tier}>
                <td>{o.name}</td>
                <td className="r num muted">{money(o.minPrincipalCents)}</td>
                <td className="r num muted">{money(o.maxPrincipalCents)}</td>
                <td className="r num">{(o.effectiveAprBps / 100).toFixed(1)}%{o.effectiveAprBps !== o.aprBps && <span className="fair-ref"> vs {(o.aprBps / 100).toFixed(1)}%</span>}</td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </section>

      {ins && (
        <section className="card">
          <div className="row-head"><h2>Insurance</h2><span className="hint">A bad day costs the deductible, not the aircraft</span></div>
          {ins.policies.length > 0 && (
            <div className="tbl-wrap"><table className="tbl">
              <thead><tr><th>Aircraft</th><th className="r">Cover</th><th className="r">Premium/wk</th><th className="r">Payout</th><th></th></tr></thead>
              <tbody>{ins.policies.map(p => (
                <tr key={p.id}>
                  <td>{p.tail} <span className="muted">· {p.aircraftName} · {Math.round(p.conditionMilli / 1000)}%</span></td>
                  <td className="r num muted">{Math.round(p.coverageMilli / 1000)}%</td>
                  <td className="r num">{money(p.premiumPerWeekCents)}</td>
                  <td className="r num">{money(p.claimPayoutCents)}</td>
                  <td className="r">
                    {p.claimable
                      ? <button disabled={busy} onClick={() => claim(p.id)}>File claim</button>
                      : <button disabled={busy} onClick={() => cancelIns(p.id)}>Cancel</button>}
                  </td>
                </tr>
              ))}</tbody>
            </table></div>
          )}
          {ins.quotes.length === 0
            ? (ins.policies.length === 0 ? <div className="empty">No aircraft to insure.</div> : null)
            : (
              <div className="tbl-wrap" style={{ marginTop: ins.policies.length ? 14 : 0 }}><table className="tbl">
                <thead><tr><th>Uninsured</th><th className="r">Premium/wk</th><th className="r">Deductible</th><th></th></tr></thead>
                <tbody>{ins.quotes.map(q => (
                  <tr key={q.aircraftInstanceId}>
                    <td>{q.tail} <span className="muted">· {q.aircraftName}</span></td>
                    <td className="r num">{money(q.premiumPerWeekCents)}</td>
                    <td className="r num muted">{money(q.deductibleCents)}</td>
                    <td className="r"><button disabled={busy} onClick={() => insure(q.aircraftInstanceId)}>Insure</button></td>
                  </tr>
                ))}</tbody>
              </table></div>
            )}
        </section>
      )}

      {/* Itemised statement + CSV export. */}
      <section className="card">
        <div className="row-head">
          <h2>Statement · {periodLabel}</h2>
          <button disabled={stmt.length === 0} onClick={exportCsv}>Export CSV</button>
        </div>
        {stmt.length === 0 ? <div className="empty">No entries in this window.</div> : (
          <div className="tbl-wrap stmt-wrap"><table className="tbl">
            <thead><tr><th>When</th><th>Category</th><th>Description</th><th>Attribution</th><th className="r">Amount</th></tr></thead>
            <tbody>{stmt.map((r, i) => (
              <tr key={i}>
                <td className="muted">{when(r.at)}</td>
                <td>{spaced(r.category)}</td>
                <td className="muted">{r.description}</td>
                <td className="muted">{[r.aircraft, r.staff, r.base].filter(Boolean).join(' · ') || '—'}</td>
                <td className={`r num ${r.amountCents < 0 ? 'neg' : 'pos'}`}>{money(r.amountCents)}</td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </section>
    </div>
  )
}

// ─── Logbook ─────────────────────────────────────────────────────────────────

// ─── Logbook (Phase 6: deep flight history + ledger) ─────────────────────────

// Great-circle interpolation (slerp on the unit sphere) so long legs bow correctly on the map
// instead of cutting a flat Mercator chord.
function gcPoints(a: [number, number], b: [number, number], segs = 48): [number, number][] {
  const rad = Math.PI / 180, deg = 180 / Math.PI
  const lat1 = a[0] * rad, lon1 = a[1] * rad, lat2 = b[0] * rad, lon2 = b[1] * rad
  const dLat = lat2 - lat1, dLon = lon2 - lon1
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2
  const dist = 2 * Math.asin(Math.min(1, Math.sqrt(h)))
  if (dist === 0) return [a, b]
  const out: [number, number][] = []
  for (let i = 0; i <= segs; i++) {
    const t = i / segs
    const A = Math.sin((1 - t) * dist) / Math.sin(dist)
    const B = Math.sin(t * dist) / Math.sin(dist)
    const x = A * Math.cos(lat1) * Math.cos(lon1) + B * Math.cos(lat2) * Math.cos(lon2)
    const y = A * Math.cos(lat1) * Math.sin(lon1) + B * Math.cos(lat2) * Math.sin(lon2)
    const z = A * Math.sin(lat1) + B * Math.sin(lat2)
    out.push([Math.atan2(z, Math.sqrt(x * x + y * y)) * deg, Math.atan2(y, x) * deg])
  }
  return out
}

function hoursText(h: number): string {
  const m = Math.round(h * 60)
  return m >= 60 ? `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m` : `${m}m`
}
// Tone for a touchdown rate — green for smooth, red for hard, neutral between.
function landTone(fpm: number): string { const f = Math.abs(fpm); return f <= 180 ? 'pos' : f <= 360 ? '' : 'neg' }

// Per-category identity for the ledger: a hue + an original stroke glyph, so the ledger reads at a
// glance instead of as a wall of text. Keyed by the LedgerCategory enum name the API sends.
const LEDGER_HUE: Record<string, string> = {
  JobPayout: '#3ecf8e', JobBonus: '#3ecf8e', CampaignReward: '#39b56a', InsuranceClaim: '#3ecf8e',
  StartingBalance: '#8a97a7', LoanPrincipal: '#6d84ff', Trade: '#2bb6c4', Transfer: '#8a97a7', Adjustment: '#8a97a7',
  AirportFee: '#e0912f', Penalty: '#f26a5c', Fuel: '#d9a11c', Repair: '#e0912f', CheckFlightFee: '#8b7be8',
  AircraftPurchase: '#6d84ff', AircraftRental: '#6d84ff', BaseRent: '#d9b84a', StaffWage: '#39b56a',
  LoanInterest: '#f26a5c', LoanPayment: '#6d84ff', InsurancePremium: '#8b7be8',
}
function ledgerHue(cat: string): string { return LEDGER_HUE[cat] ?? 'var(--accent)' }
function ledgerIcon(cat: string) {
  switch (cat) {
    case 'JobPayout': case 'JobBonus': case 'CampaignReward': case 'InsuranceClaim':
      return <><circle cx="12" cy="12" r="8" /><path d="M12 8v8M9.5 10.5h4a1.5 1.5 0 0 1 0 3h-3a1.5 1.5 0 0 0 0 3h4" /></>
    case 'AirportFee': case 'Penalty': case 'CheckFlightFee':
      return <><path d="M12 4l9 16H3z" /><path d="M12 10v4M12 17.2h0" /></>
    case 'Fuel': return <><path d="M7 20V6a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v14M5 20h12" /><path d="M15 9h2.5a1.5 1.5 0 0 1 1.5 1.5V15a1.5 1.5 0 0 0 1.5 1.5" /></>
    case 'Repair': return <><path d="M14 6a3.5 3.5 0 0 0-4.7 4.5L4 15.8V20h4.2l5.3-5.3A3.5 3.5 0 0 0 18 10l-2.3 2.3-2-2z" /></>
    case 'AircraftPurchase': case 'AircraftRental':
      return <path d="M12 3c.6 0 1 .9 1 2.2v4.2l7 4v1.6l-7-2.3v3.7l2 1.4v1.2L12 20l-3-1.8v-1.2l2-1.4V12.9L4 15.2V13.6l7-4V5.2C11 3.9 11.4 3 12 3z" />
    case 'BaseRent': return <><rect x="4" y="8" width="16" height="12" rx="1" /><path d="M4 8l8-4 8 4M9 20v-5h6v5" /></>
    case 'StaffWage': return <><circle cx="12" cy="8" r="3.2" /><path d="M5.5 20c0-3.4 2.9-5.5 6.5-5.5s6.5 2.1 6.5 5.5" /></>
    case 'LoanPrincipal': case 'LoanInterest': case 'LoanPayment':
      return <><path d="M4 10l8-5 8 5" /><path d="M5 10v8M12 10v8M19 10v8M4 20h16" /></>
    case 'InsurancePremium': return <><path d="M12 3l7 3v5c0 5-3 8-7 10-4-2-7-5-7-10V6z" /><path d="M9 11.5l2 2 4-4" /></>
    case 'Trade': return <><path d="M4 9h13l-3-3M20 15H7l3 3" /></>
    default: return <><circle cx="12" cy="12" r="8" /><path d="M8 12h8" /></>
  }
}

type FlightSort = 'date' | 'payout' | 'dist' | 'dur' | 'fuel' | 'landing' | 'xp' | 'score'
type LandBand = { label: string; test: (f: number) => boolean; tone: 'pos' | 'neg' | 'accent' }
const LAND_BANDS: LandBand[] = [
  { label: 'Butter ≤60', test: f => f <= 60, tone: 'pos' },
  { label: 'Smooth ≤180', test: f => f > 60 && f <= 180, tone: 'pos' },
  { label: 'Firm ≤360', test: f => f > 180 && f <= 360, tone: 'accent' },
  { label: 'Hard ≤600', test: f => f > 360 && f <= 600, tone: 'neg' },
  { label: 'Rough >600', test: f => f > 600, tone: 'neg' },
]

// Selectable statistics for the logbook chart (NeoFly's Career → Statistics picker): a metric over a window
// of recent flights, oldest-first.
const STAT_METRICS: { key: string; label: string; tone: 'pos' | 'accent' | 'warn'; get: (f: FlightLog) => number | null; fmt: (v: number) => string }[] = [
  { key: 'score', label: 'Flight score', tone: 'pos', get: f => f.overallScore, fmt: v => `${v}` },
  { key: 'fpm', label: 'Landing fpm', tone: 'pos', get: f => Math.round(f.touchdownFpm), fmt: v => `${v} fpm` },
  { key: 'dist', label: 'Distance', tone: 'accent', get: f => Math.round(f.distanceNm), fmt: v => `${v.toLocaleString()} nm` },
  { key: 'payout', label: 'Payout', tone: 'pos', get: f => Math.round(f.payoutCents / 100), fmt: v => `$${v.toLocaleString()}` },
  { key: 'fuel', label: 'Fuel burned', tone: 'warn', get: f => Math.round(f.fuelUsedLbs), fmt: v => `${v.toLocaleString()} lb` },
]
const STAT_RANGES = [{ n: 20, label: 'Last 20' }, { n: 50, label: 'Last 50' }, { n: 0, label: 'All' }]

function Logbook({ state }: { state: State }) {
  const [metric, setMetric] = useState('score')
  const [range, setRange] = useState(50)
  const [flights, setFlights] = useState<FlightLog[]>([])
  const [ledger, setLedger] = useState<LedgerEntry[]>([])
  const [totals, setTotals] = useState<FlightTotals | null>(null)
  const [take, setTake] = useState(100)
  const [more, setMore] = useState(true)
  const [selected, setSelected] = useState<string | null>(null)
  const [types, setTypes] = useState<Set<string>>(new Set()) // empty = all missions
  const [sort, setSort] = useState<FlightSort>('date')
  const [asc, setAsc] = useState(false)
  const [ledgerCat, setLedgerCat] = useState('') // '' = all categories

  useEffect(() => {
    api.flights(0, take).then(f => { setFlights(f); setMore(f.length >= take) }).catch(() => {})
  }, [take])
  useEffect(() => {
    api.ledger(200).then(setLedger).catch(() => {})
    api.flightTotals().then(setTotals).catch(() => {})
  }, [])

  // Cash-balance curve reconstructed from the ledger window, anchored to end at current cash.
  const balances = (() => {
    const sorted = [...ledger].sort((a, b) => a.at.localeCompare(b.at))
    if (sorted.length < 2) return [] as number[]
    const net = sorted.reduce((s, e) => s + e.amountCents, 0)
    let running = state.cashCents - net
    return sorted.map(e => { running += e.amountCents; return Math.round(running / 100) })
  })()
  const fpms = [...flights].reverse().map(f => Math.round(f.touchdownFpm))
  // Phase 12 — the un-gameable flight score, oldest-first, over scored legs (the headline trend, not raw fpm).
  const scores = [...flights].reverse().map(f => f.overallScore).filter((s): s is number => s != null)
  // Phase 12 — the selectable statistic (metric + window), oldest-first over recent flights.
  const statDef = STAT_METRICS.find(m => m.key === metric) ?? STAT_METRICS[0]
  const statAll = [...flights].reverse().map(statDef.get).filter((v): v is number => v != null)
  const statSeries = range > 0 ? statAll.slice(-range) : statAll

  // Flights: mission filter + sort, with a live totals footer over the shown rows.
  const missions = Array.from(new Set(flights.map(f => f.mission).filter((m): m is string => !!m)))
  const skey = (f: FlightLog) =>
    sort === 'payout' ? f.payoutCents : sort === 'dist' ? f.distanceNm : sort === 'dur' ? f.durationHours
      : sort === 'fuel' ? f.fuelUsedLbs : sort === 'landing' ? Math.abs(f.touchdownFpm) : sort === 'xp' ? f.xp
        : sort === 'score' ? (f.overallScore ?? -1)
        : new Date(f.settledAt).getTime()
  const shown = flights
    .filter(f => types.size === 0 || (f.mission != null && types.has(f.mission)))
    .sort((a, b) => (skey(a) - skey(b)) * (asc ? 1 : -1))
  const sel = flights.find(f => f.id === selected) ?? null
  const foot = shown.reduce((s, f) => ({
    dist: s.dist + f.distanceNm, dur: s.dur + f.durationHours, fuel: s.fuel + f.fuelUsedLbs,
    pay: s.pay + f.payoutCents, xp: s.xp + f.xp, land: s.land + f.touchdownFpm,
  }), { dist: 0, dur: 0, fuel: 0, pay: 0, xp: 0, land: 0 })
  const avgLand = shown.length ? Math.round(foot.land / shown.length) : 0

  const toggleType = (t: string) => setTypes(s => { const n = new Set(s); n.has(t) ? n.delete(t) : n.add(t); return n })
  const setSortKey = (k: FlightSort) => { if (sort === k) setAsc(a => !a); else { setSort(k); setAsc(k === 'landing') } }

  // Ledger: running-balance annotation (computed over the full window, before filtering) + category filter.
  const ledgerRows = (() => {
    const ascRows = [...ledger].sort((a, b) => a.at.localeCompare(b.at))
    const net = ascRows.reduce((s, e) => s + e.amountCents, 0)
    let running = state.cashCents - net
    const withBal = ascRows.map(e => { running += e.amountCents; return { ...e, balanceCents: running } })
    return withBal.reverse().filter(e => !ledgerCat || e.category === ledgerCat)
  })()
  const ledgerCats = Array.from(new Set(ledger.map(e => e.category)))

  return (
    <div className="logbook-screen">
      {/* Lifetime totals strip */}
      {totals && totals.flights > 0 && (
        <div className="hero-stats logbook-totals">
          <HeroStat label="Flights" value={totals.flights.toLocaleString()} accent />
          {totals.scoredFlights > 0 && <HeroStat label="Avg flight score" value={`${Math.round(totals.avgScore)}`} accent hint={`over ${totals.scoredFlights} scored`} />}
          {totals.scoredFlights > 0 && <HeroStat label="Best score" value={`${totals.bestScore}`} />}
          <HeroStat label="Hours flown" value={totals.totalHours.toFixed(1)} unit="h" />
          <HeroStat label="Distance" value={Math.round(totals.totalDistanceNm).toLocaleString()} unit="nm" />
          <HeroStat label="Avg landing" value={`${Math.round(totals.avgTouchdownFpm)}`} unit="fpm" />
          <HeroStat label="Lifetime earnings" value={money(totals.lifetimeEarningsCents)} />
        </div>
      )}

      {(balances.length > 1 || scores.length > 1 || fpms.length > 1) && (
        <section className="card">
          <h2>Trends</h2>
          <div className="trends">
            <div className="trend-cell">
              <div className="trend-head"><span className="metalabel">Cash balance</span><span className="num">{money(state.cashCents)}</span></div>
              <Trendline values={balances} tone="accent" />
            </div>
            <div className="trend-cell">
              <div className="trend-head">
                <span className="stat-picks">
                  <select className="stat-sel" value={metric} onChange={e => setMetric(e.target.value)}>
                    {STAT_METRICS.map(m => <option key={m.key} value={m.key}>{m.label}</option>)}
                  </select>
                  <select className="stat-sel" value={range} onChange={e => setRange(Number(e.target.value))}>
                    {STAT_RANGES.map(r => <option key={r.n} value={r.n}>{r.label}</option>)}
                  </select>
                </span>
                <span className="num">{statSeries.length ? statDef.fmt(statSeries[statSeries.length - 1]) : '—'}</span>
              </div>
              {statSeries.length > 1
                ? <Trendline values={statSeries} tone={statDef.tone} />
                : <div className="empty" style={{ padding: 12 }}>Not enough flights yet for this stat.</div>}
            </div>
          </div>
        </section>
      )}

      {flights.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Landing performance</h2><span className="hint">last {flights.length} legs · best {totals ? Math.round(totals.bestTouchdownFpm) : '—'} fpm</span></div>
          <div className="bars">
            {LAND_BANDS.map(b => {
              const count = flights.filter(f => b.test(Math.abs(f.touchdownFpm))).length
              return <BarRow key={b.label} label={b.label} value={count} max={flights.length} tone={b.tone} />
            })}
          </div>
        </section>
      )}

      <section className="card">
        <div className="row-head">
          <h2>Flights <span className="muted">· {shown.length} of {flights.length}</span></h2>
        </div>
        {flights.length === 0 ? <div className="empty">No flights logged yet — accept a job and fly it.</div> : (
          <>
            {missions.length > 1 && (
              <div className="jf-types logbook-filters">
                {missions.map(t => {
                  const m = missionMeta(t); const on = types.size === 0 || types.has(t)
                  return (
                    <button key={t} type="button" className={`jf-type ${on ? 'on' : ''}`} style={on ? { borderColor: m.color, color: m.color } : undefined} onClick={() => toggleType(t)}>
                      <svg viewBox="0 0 24 24">{missionIcon(t)}</svg>{m.label}
                    </button>
                  )
                })}
              </div>
            )}
            <div className="jobs-work logbook-work">
              <div className="jobs-tablewrap">
                <table className="tbl logbook-table">
                  <thead><tr>
                    <th>Route</th>
                    <th>Aircraft</th>
                    <SortTh label="Score" k="score" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="Dist" k="dist" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="Time" k="dur" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="Fuel" k="fuel" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="Touchdown" k="landing" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="Payout" k="payout" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="XP" k="xp" sort={sort} asc={asc} onSort={setSortKey} />
                    <SortTh label="When" k="date" sort={sort} asc={asc} onSort={setSortKey} />
                  </tr></thead>
                  <tbody>
                    {shown.map(f => {
                      const m = missionMeta(f.mission ?? '')
                      return (
                        <tr key={f.id} className={`jrow ${selected === f.id ? 'on' : ''}`} onClick={() => setSelected(f.id)}>
                          <td><span className="jrow-type" style={{ background: m.color }} title={f.mission ? m.label : 'Flight'} /><span className="loc">{f.origin}</span> <span className="muted">→</span> <span className="loc">{f.dest}</span></td>
                          <td className="muted"><span className="lb-ac"><AircraftImage typeId={f.aircraftTypeId ?? undefined} mini />{f.tail ? <span className="loc">{f.tail}</span> : <span className="lb-ac-title">{f.aircraftTitle}</span>}</span></td>
                          <td className={`r num ${f.overallScore == null ? 'muted' : f.scoreValid === false ? 'neg' : scoreTone(f.overallScore)}`} title={f.scoreValid === false ? 'Score voided' : undefined}>{f.overallScore ?? '—'}</td>
                          <td className="r num">{Math.round(f.distanceNm).toLocaleString()}</td>
                          <td className="r num">{hoursText(f.durationHours)}</td>
                          <td className="r num">{Math.round(f.fuelUsedLbs).toLocaleString()}</td>
                          <td className={`r num ${landTone(f.touchdownFpm)}`}>{signed(Math.round(f.touchdownFpm))}</td>
                          <td className="r num pos">{money(f.payoutCents)}</td>
                          <td className="r num">+{f.xp}</td>
                          <td className="r muted">{when(f.settledAt)}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                  <tfoot>
                    <tr className="logbook-foot">
                      <td>{shown.length} legs</td>
                      <td />
                      <td className="r num">{Math.round(foot.dist).toLocaleString()}</td>
                      <td className="r num">{hoursText(foot.dur)}</td>
                      <td className="r num">{Math.round(foot.fuel).toLocaleString()}</td>
                      <td className="r num">{avgLand} <span className="muted">avg</span></td>
                      <td className="r num pos">{money(foot.pay)}</td>
                      <td className="r num">+{foot.xp}</td>
                      <td />
                    </tr>
                  </tfoot>
                </table>
                {more && <div className="logbook-more"><button className="ghost" onClick={() => setTake(t => t + 100)}>Load older flights</button></div>}
              </div>
              <div className="jobs-side">
                {sel ? <FlightDetail id={sel.id} /> : <div className="card jobs-pick"><div className="empty">Select a flight for the full record.</div></div>}
                <LogbookMap legs={shown} selectedId={selected} onSelect={setSelected} />
              </div>
            </div>
          </>
        )}
      </section>

      <section className="card">
        <div className="row-head">
          <h2>Ledger</h2>
          {ledgerCats.length > 1 && (
            <select className="ledger-filter" value={ledgerCat} onChange={e => setLedgerCat(e.target.value)}>
              <option value="">All categories</option>
              {ledgerCats.map(c => <option key={c} value={c}>{spaced(c)}</option>)}
            </select>
          )}
        </div>
        {ledgerRows.length === 0 ? <div className="empty">No entries yet.</div> : (
          <table className="tbl ledger-table">
            <thead><tr><th>Category</th><th>Description</th><th className="r">Amount</th><th className="r">Balance</th><th className="r">When</th></tr></thead>
            <tbody>
              {ledgerRows.map((e, i) => (
                <tr key={i}>
                  <td>
                    <span className="ledger-cat">
                      <span className="ledger-glyph" style={{ color: ledgerHue(e.category), background: `color-mix(in srgb, ${ledgerHue(e.category)} 15%, transparent)` }}>
                        <svg viewBox="0 0 24 24">{ledgerIcon(e.category)}</svg>
                      </span>
                      {spaced(e.category)}
                    </span>
                  </td>
                  <td className="muted">{e.description}</td>
                  <td className={`r num ${e.amountCents < 0 ? 'neg' : 'pos'}`}>{money(e.amountCents)}</td>
                  <td className="r num muted">{money(e.balanceCents)}</td>
                  <td className="r muted">{when(e.at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

// The full record for one flight — mission, aircraft imagery, derived stats, and the itemised payout
// (fetched from /api/flights/{id}, whose lines come straight from the frozen PayoutBreakdownJson).
function FlightDetail({ id }: { id: string }) {
  const [d, setD] = useState<FlightDetail | null>(null)
  const [loading, setLoading] = useState(true)
  useEffect(() => {
    let live = true
    setLoading(true); setD(null)
    api.flight(id).then(x => { if (live) setD(x) }).catch(() => {}).finally(() => { if (live) setLoading(false) })
    return () => { live = false }
  }, [id])

  if (loading) return <div className="card jdetail"><div className="empty">Loading record…</div></div>
  if (!d) return <div className="card jdetail"><div className="empty">Couldn't load this flight.</div></div>

  const m = missionMeta(d.mission ?? '')
  const geo = (d.originLat || d.originLon) && (d.destLat || d.destLon)
  const hdg = geo ? Math.round(bearing([d.originLat, d.originLon], [d.destLat, d.destLon])) : null
  const kts = d.durationHours > 0 ? Math.round(d.distanceNm / d.durationHours) : 0
  const lbnm = d.distanceNm > 0 ? d.fuelUsedLbs / d.distanceNm : 0

  return (
    <div className="card jdetail flt-detail">
      <div className="mission-head">
        <span className="mission-badge" style={{ background: `color-mix(in srgb, ${m.color} 16%, transparent)`, color: m.color }}><svg viewBox="0 0 24 24">{missionIcon(d.mission ?? '')}</svg></span>
        <div className="mission-title">
          <div className="mission-type">{d.mission ? m.label : 'Flight'}</div>
          <div className="mission-route"><b>{d.origin}</b> <span className="arrow">→</span> <b>{d.dest}</b></div>
          {d.outcomeGrade != null && (
            <div className={`mission-grade ${d.outcomeGrade === 3 ? 'failed' : 'partial'}`}>
              {d.outcomeGrade === 3 ? 'Delivery failed' : 'Partial delivery'}{d.outcomeReason ? ` · ${d.outcomeReason}` : ''}
            </div>
          )}
        </div>
      </div>
      <AircraftImage typeId={d.aircraftTypeId ?? undefined} category={d.aircraftCategory ?? undefined} />
      <div className="jd-dest">{d.tail && <span className="loc">{d.tail} · </span>}{d.aircraftName ?? d.aircraftTitle}</div>
      <div className="flt-route-names muted">{d.originName} → {d.destName}</div>
      <div className="jd-grid flt-grid">
        <div><span className="metalabel">Distance</span><span className="num">{Math.round(d.distanceNm).toLocaleString()} nm</span></div>
        <div><span className="metalabel">Block time</span><span className="num">{hoursText(d.durationHours)}</span></div>
        <div><span className="metalabel">Avg speed</span><span className="num">{kts} kt</span></div>
        <div><span className="metalabel">Fuel used</span><span className="num">{Math.round(d.fuelUsedLbs).toLocaleString()} lb</span></div>
        <div><span className="metalabel">Fuel burn</span><span className="num">{lbnm.toFixed(1)} lb/nm</span></div>
        {hdg !== null && <div><span className="metalabel">Bearing</span><span className="num">{String(hdg).padStart(3, '0')}°</span></div>}
        <div><span className="metalabel">Touchdown</span><span className={`num ${landTone(d.touchdownFpm)}`}>{signed(Math.round(d.touchdownFpm))} <span className="muted">{landingWord(d.touchdownFpm)}</span></span></div>
        <div><span className="metalabel">XP</span><span className="num">+{d.xp}</span></div>
        {d.pax != null && d.pax > 0
          ? <div><span className="metalabel">Passengers</span><span className="num">{d.pax}</span></div>
          : d.weightLbs != null && d.weightLbs > 0
            ? <div><span className="metalabel">Payload</span><span className="num">{d.weightLbs.toLocaleString()} lb</span></div>
            : null}
      </div>
      <FlightScoreDebrief d={d} />
      <div className="jd-pay flt-pay">
        <div className="metalabel flt-pay-head">Payout breakdown</div>
        {d.lines.length === 0 ? <div className="muted" style={{ fontSize: 13 }}>No itemised breakdown recorded.</div>
          : d.lines.map((l, i) => (
            <div className="jd-payrow" key={i}>
              <span className={l.amountCents < 0 ? 'muted' : ''}>{l.label}</span>
              <span className={`num ${l.amountCents < 0 ? 'neg' : 'pos'}`}>{money(l.amountCents)}</span>
            </div>
          ))}
        <div className="jd-payrow jd-net"><span>Net payout</span><span className="num">{money(d.payoutCents)}</span></div>
      </div>
      {d.events.length > 0 && (
        <div className="flt-events">
          <div className="metalabel flt-pay-head">Flight log</div>
          <div className="flt-events-body">
            {d.events.map((e, i) => (
              <div className={`flog-row ${evSev(e.severity)}`} key={i}>
                <span className="flog-time num">{new Date(e.at).toLocaleTimeString([], { hour12: false })}</span>
                <span className="flog-mark" />
                <span className="flog-text">{e.message}</span>
              </div>
            ))}
          </div>
        </div>
      )}
      <div className="flt-when muted">Departed {when(d.departedAt)} · settled {when(d.settledAt)}</div>
    </div>
  )
}

// The route network: every flown leg as a great-circle line coloured by mission, endpoints pinned,
// the selected leg lifted. Esri satellite tiles (keyless), rebuilt only when the leg set changes.
function LogbookMap({ legs, selectedId, onSelect }: { legs: FlightLog[]; selectedId: string | null; onSelect: (id: string) => void }) {
  const host = useRef<HTMLDivElement>(null)
  const lines = useRef<Record<string, { line: L.Polyline; color: string }>>({})
  const online = typeof navigator === 'undefined' ? true : navigator.onLine
  const plotted = legs.filter(l => (l.originLat !== 0 || l.originLon !== 0) && (l.destLat !== 0 || l.destLon !== 0))
  const sig = plotted.map(l => l.id).join('|')
  const onSelRef = useRef(onSelect); onSelRef.current = onSelect

  useEffect(() => {
    if (!host.current || !online || plotted.length === 0) return
    const map = L.map(host.current, { attributionControl: true, zoomControl: true, worldCopyJump: true })
    L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', { attribution: 'Imagery &copy; Esri, Maxar, Earthstar Geographics', maxZoom: 18 }).addTo(map)
    lines.current = {}
    const group: L.Layer[] = []
    const seen = new Set<string>()
    for (const l of plotted) {
      const m = missionMeta(l.mission ?? '')
      const line = L.polyline(gcPoints([l.originLat, l.originLon], [l.destLat, l.destLon]), { color: m.color, weight: 2, opacity: .7 }).addTo(map)
      line.on('click', () => onSelRef.current(l.id))
      line.bindTooltip(`${l.origin} → ${l.dest} · ${money(l.payoutCents)}`, { direction: 'top', className: 'sat-tip', sticky: true })
      lines.current[l.id] = { line, color: m.color }
      group.push(line)
      for (const [lat, lon, code] of [[l.originLat, l.originLon, l.origin], [l.destLat, l.destLon, l.dest]] as const) {
        const key = `${lat.toFixed(3)},${lon.toFixed(3)}`
        if (seen.has(key)) continue
        seen.add(key)
        const dot = L.circleMarker([lat, lon], { radius: 3.5, weight: 1.5, color: '#e9eef5', fillColor: '#8a97a7', fillOpacity: .85 }).addTo(map)
        dot.bindTooltip(String(code), { direction: 'top', className: 'sat-tip' })
        group.push(dot)
      }
    }
    map.fitBounds(L.featureGroup(group).getBounds().pad(0.25), { maxZoom: 8 })
    const t = setTimeout(() => map.invalidateSize(), 60)
    return () => { clearTimeout(t); map.remove(); lines.current = {} }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, online])

  useEffect(() => {
    for (const [id, { line, color }] of Object.entries(lines.current)) {
      const on = id === selectedId
      line.setStyle({ weight: on ? 4 : 2, opacity: on ? 1 : .55, color: on ? '#ffffff' : color })
      if (on) line.bringToFront()
    }
  }, [selectedId, sig])

  if (!online) return <div className="card"><div className="empty" style={{ padding: 16 }}>Map needs a connection.</div></div>
  if (plotted.length === 0) return <div className="card"><div className="empty" style={{ padding: 16 }}>No mapped legs yet.</div></div>
  return <div className="satmap logbookmap" ref={host} role="img" aria-label="Route network map" />
}

// ─── Airline identity (Phase 5c) ─────────────────────────────────────────────

// An original, generated roundel: an accent disc with one of a fixed set of white geometric marks.
function Emblem({ emblem, color, size = 44 }: { emblem: string; color: string; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 48 48" className="emblem" aria-hidden="true">
      <circle cx="24" cy="24" r="23" fill={color} />
      <EmblemMark k={emblem} />
    </svg>
  )
}

function EmblemMark({ k }: { k: string }) {
  const s = { fill: 'none', stroke: '#fff', strokeWidth: 3, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  switch (k) {
    case 'delta': return <path d="M24 13 L35 34 L13 34 Z" {...s} />
    case 'wing': return <path d="M11 31 Q24 16 37 31" {...s} />
    case 'peak': return <path d="M11 32 L19 20 L26 28 L32 20 L37 32" {...s} />
    case 'star': return <path d="M24 12 l3.6 7.3 8 1.2 -5.8 5.7 1.4 8 -7.2 -3.8 -7.2 3.8 1.4 -8 -5.8 -5.7 8 -1.2 Z" fill="#fff" />
    case 'compass': return <path d="M24 11 L27.5 24 L24 37 L20.5 24 Z M11 24 L24 20.5 L37 24 L24 27.5 Z" fill="#fff" />
    case 'roundel':
    default: return <g><circle cx="24" cy="24" r="11" {...s} /><circle cx="24" cy="24" r="4" fill="#fff" /></g>
  }
}

// Phase 11a — the airline's operating reputation: the name your operation earns, moved by your own flying
// (by score) and your crews' (toward their skill). A two-source split + a short log; a fall is coached, never
// a red penalty (the Fun Dial, L9). It feeds the airline standing above; later phases make it pay.
function AirlineReputationCard({ rep, color }: { rep: AirlineData['reputation']; color: string }) {
  const val = rep.operatingReputationMilli / 1000
  const fmt = (m: number) => `${m >= 0 ? '+' : '-'}${(Math.abs(m) / 1000).toFixed(1)}`
  const crewDragging = rep.recentCrewDeltaMilli < 0 && rep.recentCrewDeltaMilli <= rep.recentPlayerDeltaMilli
  return (
    <section className="card">
      <div className="row-head"><h2>Airline reputation</h2><span className="hint">the name your operation earns</span></div>
      <div className="airep-head">
        <div className="airep-score num" style={{ color }}>{val.toFixed(1)}<span className="airep-max"> / 100</span></div>
        <div className="airep-split">
          <span className="airep-src">From your flying <b className="num">{fmt(rep.recentPlayerDeltaMilli)}</b></span>
          <span className="airep-src">From your crew <b className="num">{fmt(rep.recentCrewDeltaMilli)}</b></span>
        </div>
      </div>
      <div className="rank-bar"><div className="rank-fill" style={{ width: `${Math.min(100, val)}%`, background: color }} /></div>
      {rep.recent.length > 0 && (
        <table className="tbl" style={{ marginTop: 12 }}>
          <tbody>{rep.recent.map((e, i) => (
            <tr key={i}>
              <td><span className={`airep-tag airep-${e.source.toLowerCase()}`}>{e.source === 'Player' ? 'You' : 'Crew'}</span> {e.reason}</td>
              <td className={`r num ${e.deltaMilli >= 0 ? 'pos' : 'airep-neg'}`}>{fmt(e.deltaMilli)}</td>
            </tr>
          ))}</tbody>
        </table>
      )}
      <p className={crewDragging ? 'airep-coach' : 'hint'}>{crewDragging
        ? 'Greener crews are dragging your name down — invest in sharper crew, or fly the marquee legs yourself to lift it.'
        : 'Your own legs move it by their score; your crews’ by their skill. A stronger name lifts your airline standing.'}</p>
    </section>
  )
}

function Airline({ onSaved }: { onSaved: () => void }) {
  const [data, setData] = useState<AirlineData | null>(null)
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [color, setColor] = useState('#4f46e5')
  const [emblem, setEmblem] = useState('roundel')
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()

  const load = useCallback(async () => {
    const d = await api.airline()
    setData(d)
    setName(d.identity.name); setCode(d.identity.tailCode); setColor(d.identity.accentColorHex); setEmblem(d.identity.emblemKey)
  }, [])
  useEffect(() => { load().catch(e => setMsg(cleanErr(e))) }, [load])

  const save = async () => {
    setBusy(true); setMsg(null)
    try { await api.setAirline({ name, tailCode: code, accentColorHex: color, emblemKey: emblem }); await load(); onSaved(); setMsg('Airline identity saved.') }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  if (!data) return <div className="empty">Loading…</div>
  const st = data.standing
  const nm = st.nextMove
  const pct = nm ? nm.progressPct : 100
  const nextStage = nm ? st.stages[nm.stageIndex] : null

  return (
    <div className="grid">
      <section className="card">
        <h2>Airline identity</h2>
        <div className="airline-head">
          <Emblem emblem={emblem} color={color} size={76} />
          <div>
            <div className="airline-name">{name || 'Your airline'}</div>
            <div className="muted"><span className="loc">{code || '—'}</span> · {data.identity.customised ? 'operator code' : 'suggested — make it yours'}</div>
          </div>
        </div>
                <div className="airline-form">
          <label>Airline name<input value={name} maxLength={60} onChange={e => setName(e.target.value)} /></label>
          <label>Tail code<input className="tail-in" value={code} maxLength={3} onChange={e => setCode(e.target.value.toUpperCase())} /></label>
          <label className="color-lbl">Accent<input type="color" value={color} onChange={e => setColor(e.target.value)} /></label>
        </div>
        <div className="emblem-picker">
          {data.emblems.map(k => (
            <button key={k} type="button" className={`emblem-opt ${emblem === k ? 'on' : ''}`} onClick={() => setEmblem(k)} title={k}>
              <Emblem emblem={k} color={color} size={38} />
            </button>
          ))}
        </div>
        <button className="primary" disabled={busy} onClick={save}>Save identity</button>
      </section>

      <section className="card">
        <div className="row-head"><h2>Career ladder</h2><span className="tier-badge" style={{ background: `color-mix(in srgb, ${color} 16%, transparent)`, color }}>{st.stageName}</span></div>

        {/* The whole climb at a glance — Contract Operator to Flag Carrier */}
        <div className="stage-rail">
          {st.stages.map(s => (
            <span key={s.index}
              className={`stage-chip ${s.index === st.stage ? 'current' : s.reached ? 'reached' : 'future'}`}
              style={s.reached ? { background: `color-mix(in srgb, ${color} 22%, transparent)`, color } : undefined}>
              {s.name}</span>
          ))}
        </div>

        {nm && nextStage ? (<>
          <div className="rank-bar"><div className="rank-fill" style={{ width: `${pct}%`, background: color }} /></div>
          <div className="rank-scale"><span className="num">{nm.metCount} of {nm.totalCount} for {nm.stageName}</span></div>
          <p className="next-move" style={{ color }}>Your next move: {nm.label}</p>

          <ul className="req-list">
            {nextStage.requirements.map(r => (
              <li key={r.metric} className={`${r.met ? 'met' : ''} ${r.metric === nm.metric ? 'binding' : ''}`}>
                <span className="req-tick">{r.met ? '✓' : '○'}</span>
                <span className="req-label">{r.label}</span>
                <span className="req-val num">{r.display}</span>
              </li>))}
          </ul>

          <div className="unlock-list">
            {nextStage.unlocks.map((u, i) => (
              <p key={i} className={`unlock ${u.live ? 'live' : 'horizon'}`}>
                <span className="unlock-tag">{u.live ? 'Open now' : 'On the horizon'}</span> {u.text}</p>))}
          </div>
        </>) : (
          <p className="next-move" style={{ color }}>Top of the ladder — you fly the flag.
            {st.stages[4].unlocks.filter(u => u.live).map((u, i) => <span key={i}> {u.text}</span>)}</p>
        )}

        {st.contributions.length > 0 && (
          <table className="tbl" style={{ marginTop: 14 }}>
            <tbody>{st.contributions.map(c => (
              <tr key={c.label}><td>{c.label}</td><td className="r num pos">+{c.points}</td></tr>
            ))}</tbody>
          </table>
        )}
        <p className="hint">Your operating score — what your whole operation adds up to. Computed live, never stored.</p>
      </section>

      <AirlineReputationCard rep={data.reputation} color={color} />

      <Certificates />
    </div>
  )
}

// Operating certificates (Phase 8e): the regulated licences that gate premium categories of work.
function Certificates() {
  const [certs, setCerts] = useState<CertificateStatus[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const setMsg = useToast()

  const load = useCallback(async () => { setCerts(await api.certificates()) }, [])
  useEffect(() => { load().catch(e => setMsg(cleanErr(e))) }, [load])

  const apply = async (kind: string) => {
    setBusy(kind); setMsg(null)
    try { await api.applyCertificate(kind); await load(); setMsg('Certificate issued.') }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(null) }
  }

  if (!certs) return null
  return (
    <section className="card cert-card">
      <h2>Operating certificates</h2>
      <p className="hint">Regulated licences that authorise premium work. Earn one with a fee and a standards bar — then renew it before it lapses.</p>
            <div className="cert-list">
        {certs.map(c => <CertRow key={c.kind} c={c} busy={busy === c.kind} onApply={() => apply(c.kind)} />)}
      </div>
    </section>
  )
}

function CertRow({ c, busy, onApply }: { c: CertificateStatus; busy: boolean; onApply: () => void }) {
  const state = c.valid ? 'valid' : c.held ? 'expired' : 'none'
  const badge = state === 'valid' ? `Valid · ${c.daysLeft}d left` : state === 'expired' ? 'Expired — renew' : 'Not held'
  return (
    <div className={`cert-row ${state}`}>
      <div className="cert-main">
        <div className="cert-name">{c.displayName} <span className={`cert-badge ${state}`}>{badge}</span></div>
        <div className="cert-blurb muted">{c.blurb}</div>
        <div className="cert-gates"><span className="muted">Unlocks</span> {c.gatesLabels.join(', ')}</div>
      </div>
      <div className="cert-action">
        {!c.valid && (
          <div className="cert-bar">
            <span className={c.meetsReputation ? 'ok' : 'no'}>Rep {(c.reputationMilli / 1000).toFixed(1)} / {(c.minReputationMilli / 1000).toFixed(1)}</span>
            <span className={c.meetsRecord ? 'ok' : 'no'}>{c.completedFlights} / {c.minCompletedFlights} deliveries</span>
          </div>
        )}
        <button className="primary" disabled={busy || !c.canApply} onClick={onApply}>
          {busy ? '…' : `${c.valid ? 'Renew' : 'Apply'} · ${money(c.feeCents)}`}
        </button>
        {!c.canApply && c.blocker && <div className="cert-blocker">{c.blocker}</div>}
      </div>
    </div>
  )
}

// ─── Campaigns (authored story arcs) ─────────────────────────────────────────

function Campaigns({ onChanged }: { onChanged: () => void }) {
  const [items, setItems] = useState<Campaign[] | null>(null)
  // Loading evaluates arcs server-side and may pay a completion reward, so refresh the top bar after.
  useEffect(() => { api.campaigns().then(cs => { setItems(cs); onChanged() }).catch(() => setItems([])) }, [onChanged])

  if (items === null) return <div className="empty">Loading…</div>

  return (
    <div className="grid">
      <p className="hint">Story arcs — each a ladder of goals that pays out when you finish it. They track your real progress, so steps tick off as you play.</p>
      {items.map(c => <CampaignCard key={c.key} c={c} />)}
    </div>
  )
}

function CampaignRing({ index, count }: { index: number; count: number }) {
  const pct = count > 0 ? Math.min(100, (index / count) * 100) : 0
  const r = 15, circ = 2 * Math.PI * r
  return (
    <div className="camp-ring">
      <svg viewBox="0 0 36 36" aria-label={`${index} of ${count} steps`}>
        <circle className="camp-ring-track" cx="18" cy="18" r={r} />
        <circle className="camp-ring-arc" cx="18" cy="18" r={r} strokeDasharray={circ} strokeDashoffset={circ * (1 - pct / 100)} transform="rotate(-90 18 18)" />
        <text x="18" y="18" className="camp-ring-txt num">{index}/{count}</text>
      </svg>
    </div>
  )
}

function CampaignCard({ c }: { c: Campaign }) {
  return (
    <section className={`card campaign ${c.completed ? 'done' : ''}`}>
      <div className="row-head">
        <h2>{c.name}</h2>
        {c.completed ? <span className="pill-done">Completed ✓</span> : <CampaignRing index={c.stepIndex} count={c.stepCount} />}
      </div>
      <p className="muted camp-desc">{c.description}</p>
      <ol className="camp-steps">
        {c.steps.map((s, i) => {
          const current = !c.completed && i === c.stepIndex
          const pct = s.target > 0 ? Math.min(100, (s.progress / s.target) * 100) : 0
          return (
            <li key={i} className={`camp-step ${s.done ? 'done' : current ? 'current' : 'locked'}`}>
              <span className="camp-m">{s.done ? '✓' : current ? '▸' : '○'}</span>
              <div className="camp-body">
                <div className="camp-title">{s.title}</div>
                <div className="camp-detail muted">{s.detail}</div>
                {current && s.target > 1 && (
                  <div className="camp-bar" title={`${s.progress} / ${s.target}`}><div className="camp-fill" style={{ width: `${pct}%` }} /></div>
                )}
              </div>
            </li>
          )
        })}
      </ol>
      <div className="camp-foot">
        <span className="hint">Reward on completion</span>
        <span className={`num ${c.completed ? 'pos' : ''}`}>{money(c.rewardCents)}{c.completed ? ' · paid' : ''}</span>
      </div>
    </section>
  )
}

// ─── Awards (achievements) ───────────────────────────────────────────────────

function Awards() {
  const [items, setItems] = useState<Achievement[] | null>(null)
  useEffect(() => { api.achievements().then(setItems).catch(() => setItems([])) }, [])

  if (items === null) return <div className="empty">Loading…</div>
  const earned = items.filter(a => a.earned).length
  const categories = Array.from(new Set(items.map(a => a.category)))

  return (
    <div className="grid">
      <section className="card">
        <div className="row-head">
          <h2>Achievements</h2>
          <span className="num rep-score">{earned} <span className="muted">/ {items.length}</span></span>
        </div>
        {categories.map(cat => (
          <div key={cat} className="ach-cat">
            <h3 className="sub-h">{cat}</h3>
            <div className="ach-grid">
              {items.filter(a => a.category === cat).map(a => <Badge key={a.key} a={a} />)}
            </div>
          </div>
        ))}
      </section>
    </div>
  )
}

function Badge({ a }: { a: Achievement }) {
  const pct = a.earned ? 100 : a.target > 0 ? Math.min(100, (a.progress / a.target) * 100) : 0
  const r = 20, circ = 2 * Math.PI * r
  return (
    <div className={`ach ${a.earned ? 'earned' : ''}`}>
      <div className="ach-medal">
        <svg viewBox="0 0 48 48" aria-hidden="true">
          <circle className="ach-ring-track" cx="24" cy="24" r={r} />
          <circle className="ach-ring" cx="24" cy="24" r={r} strokeDasharray={circ} strokeDashoffset={circ * (1 - pct / 100)} transform="rotate(-90 24 24)" />
          <text x="24" y="24" className="ach-star">★</text>
        </svg>
      </div>
      <div className="ach-body">
        <div className="ach-name">{a.name}</div>
        <div className="ach-desc muted">{a.description}</div>
        {a.earned
          ? <div className="ach-when muted">{a.earnedAt ? `Earned ${when(a.earnedAt)}` : 'Earned'}</div>
          : <div className="ach-prog muted num">{a.progress} / {a.target}</div>}
      </div>
    </div>
  )
}

// ─── Community (leaderboards) ────────────────────────────────────────────────

const BOARDS = [
  { key: 'networth', label: 'Net worth' },
  { key: 'flights', label: 'Flights' },
  { key: 'reputation', label: 'Reputation' },
  { key: 'xp', label: 'XP' },
] as const

function fmtBoard(board: string, v: number): string {
  if (board === 'networth') return money(v)
  if (board === 'reputation') return (v / 1000).toLocaleString(undefined, { maximumFractionDigits: 1 })
  return v.toLocaleString()
}

function Community() {
  const [status, setStatus] = useState<CloudStatus | null>(null)
  const [board, setBoard] = useState<string>('networth')
  const [rows, setRows] = useState<LeaderboardRow[]>([])
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()

  // On open: if signed in, push our latest standing so we appear, then read our positions back.
  const init = useCallback(async () => {
    try {
      const s = await api.cloud.status(); setStatus(s)
      if (!s.signedIn) return
      await api.cloud.submitStanding().catch(() => undefined)
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void init() }, [init])

  const loadBoard = useCallback(async (b: string) => {
    try { setRows(await api.cloud.leaderboard(b)) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { if (status?.signedIn) void loadBoard(board) }, [board, status, loadBoard])

  const refresh = async () => {
    setBusy(true); setMsg(null)
    try {
      const r = await api.cloud.submitStanding()
      if (!r.ok) { setMsg(r.error ?? 'Could not submit your standing.'); return }
      await loadBoard(board)
      setMsg('Your standing is up to date.')
    } finally { setBusy(false) }
  }

  if (status && !status.signedIn) {
    return (
      <div className="grid">
        <section className="card">
          <h2>Community</h2>
          <p className="hint">Leaderboards rank every Callsign pilot by net worth, flights, reputation, and XP. <b>Sign in under Settings → Callsign Cloud</b> to join and see where you stand.</p>
        </section>
      </div>
    )
  }

  const myPos = rows.find(r => r.isYou)?.position ?? null
  const boardLabel = BOARDS.find(b => b.key === board)?.label

  return (
    <div className="grid">
      <section className="card">
        <div className="row-head">
          <h2>Leaderboards</h2>
          <button disabled={busy} onClick={refresh}>Update my standing</button>
        </div>
                <div className="seg" style={{ marginBottom: 14 }}>
          {BOARDS.map(b => (
            <button key={b.key} className={`seg-btn ${board === b.key ? 'on' : ''}`} onClick={() => setBoard(b.key)}>{b.label}</button>
          ))}
        </div>
        {myPos != null && <p className="about-line">You're <b>#{myPos.toLocaleString()}</b> on the {boardLabel?.toLowerCase()} board.</p>}
        {rows.length === 0
          ? <div className="empty">No standings yet. Be the first — “Update my standing”.</div>
          : (
            <div className="tbl-wrap"><table className="tbl">
              <thead><tr><th className="r" style={{ width: 56 }}>#</th><th>Pilot</th><th className="r">{boardLabel}</th></tr></thead>
              <tbody>{rows.map(r => (
                <tr key={r.position} className={r.isYou ? 'you' : ''}>
                  <td className="r num muted">{r.position}</td>
                  <td>{r.displayName}{r.isYou && <span className="tag" style={{ marginLeft: 8 }}>you</span>}{r.rankKey ? <span className="muted"> · {spaced(r.rankKey)}</span> : ''}</td>
                  <td className="r num">{fmtBoard(board, r.value)}</td>
                </tr>
              ))}</tbody>
            </table></div>
          )}
      </section>
    </div>
  )
}

// ─── Callsign Cloud account (sign in · cloud save) ───────────────────────────

function CloudAccount() {
  const [status, setStatus] = useState<CloudStatus | null>(null)
  const [meta, setMeta] = useState<CloudSaveMeta | null>(null)
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()
  const [staged, setStaged] = useState(false)

  const refresh = useCallback(async () => {
    try {
      const s = await api.cloud.status()
      setStatus(s)
      if (s.signedIn) { try { setMeta(await api.cloud.saveMeta()) } catch { setMeta(null) } }
      else setMeta(null)
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void refresh() }, [refresh])

  const submit = async () => {
    setBusy(true); setMsg(null)
    try {
      const r = mode === 'login'
        ? await api.cloud.login(email.trim(), password)
        : await api.cloud.register(email.trim(), name.trim(), password)
      if (!r.ok) { setMsg(r.error ?? 'Sign-in failed.'); return }
      setPassword(''); await refresh()
    } finally { setBusy(false) }
  }

  const signOut = async () => {
    setBusy(true); setMsg(null)
    try { await api.cloud.logout(); setStaged(false); await refresh() } finally { setBusy(false) }
  }

  const push = async () => {
    setBusy(true); setMsg(null)
    try {
      const r = await api.cloud.push()
      if (!r.ok) { setMsg(r.error ?? 'Upload failed.'); return }
      setMsg('Your career is backed up to the cloud.'); await refresh()
    } finally { setBusy(false) }
  }

  const pull = async () => {
    if (!window.confirm('Replace your local save with the cloud copy?\n\nYour current save is set aside as a backup, and Callsign loads the cloud one the next time it starts.')) return
    setBusy(true); setMsg(null)
    try {
      const r = await api.cloud.pull()
      if (!r.ok) { setMsg(r.error ?? 'Download failed.'); return }
      setStaged(true); setMsg('Cloud save staged.')
    } finally { setBusy(false) }
  }

  const signedIn = status?.signedIn === true

  return (
    <section className="card">
      <div className="row-head">
        <h2>Callsign Cloud</h2>
        {signedIn && <button disabled={busy} onClick={signOut}>Sign out</button>}
      </div>
            {staged && <div className="banner ok">Cloud save staged — <b>restart Callsign</b> to load it.</div>}

      {!signedIn ? (
        <>
          <p className="hint">Sign in to back up your career to the cloud and carry it to any PC. It's free, and the offline game never needs it.</p>
          <div className="seg" style={{ marginBottom: 12 }}>
            <button className={`seg-btn ${mode === 'login' ? 'on' : ''}`} onClick={() => setMode('login')}>Sign in</button>
            <button className={`seg-btn ${mode === 'register' ? 'on' : ''}`} onClick={() => setMode('register')}>Create account</button>
          </div>
          <div className="form">
            <label className="fld"><span>Email</span>
              <input type="email" value={email} autoComplete="username" placeholder="you@example.com" onChange={e => setEmail(e.target.value)} />
            </label>
            {mode === 'register' && (
              <label className="fld"><span>Display name</span>
                <input value={name} maxLength={40} placeholder="Your callsign" onChange={e => setName(e.target.value)} />
              </label>
            )}
            <label className="fld"><span>Password</span>
              <input type="password" value={password} autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
                placeholder={mode === 'register' ? 'At least 8 characters' : ''} onChange={e => setPassword(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') void submit() }} />
            </label>
            <button className="primary" disabled={busy || !email || !password || (mode === 'register' && !name)} onClick={submit}>
              {mode === 'login' ? 'Sign in' : 'Create account'}
            </button>
          </div>
        </>
      ) : (
        <>
          <p className="about-line">Signed in as <b>{status?.displayName}</b> · <span className="num">{status?.email}</span></p>
          <div className="cloud-save">
            <div className="pref-text">
              <div className="pref-label">Cloud save</div>
              <div className="hint">
                {meta?.exists
                  ? <>Last uploaded {when(meta.updatedAt ?? '')} · <span className="num">{kb(meta.sizeBytes)}</span></>
                  : 'No cloud save yet — back yours up to start.'}
              </div>
            </div>
            <span className="rowacts">
              <button className="primary" disabled={busy} onClick={push}>Back up to cloud</button>
              <button disabled={busy || !meta?.exists} onClick={pull}>Restore from cloud</button>
            </span>
          </div>
        </>
      )}
    </section>
  )
}

// ─── Settings (save backup / restore + about) ────────────────────────────────

function Settings() {
  const [ver, setVer] = useState<VersionInfo | null>(null)
  const [backups, setBackups] = useState<BackupFile[]>([])
  const [busy, setBusy] = useState(false)
  const setMsg = useToast()
  const [staged, setStaged] = useState(false)
  const [prefs, setPrefs] = useState<Prefs>(loadPrefs())
  const setPref = (patch: Partial<Prefs>) => { const next = { ...prefs, ...patch }; setPrefs(next); savePrefs(next) }

  const load = useCallback(async () => {
    try { setBackups(await api.backups()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { api.version().then(setVer).catch(() => {}); void load() }, [load])

  const backup = async () => {
    setBusy(true); setMsg(null)
    try { const b = await api.backup(); await load(); setMsg(`Backed up — ${b.name} (${kb(b.sizeBytes)}).`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const restore = async (name: string) => {
    if (!window.confirm(`Restore ${name}?\n\nYour current save is set aside as a backup, and Callsign loads the restored one the next time it starts.`)) return
    setBusy(true); setMsg(null)
    try { await api.restore(name); setStaged(true); setMsg(`Restore staged from ${name}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      <CloudAccount />
      <section className="card">
        <h2>Preferences</h2>
        <div className="pref-row">
          <div className="pref-text"><div className="pref-label">Theme</div><div className="hint">How Callsign looks. System follows your OS.</div></div>
          <div className="seg">
            {(['system', 'light', 'dark'] as Theme[]).map(t => (
              <button key={t} className={`seg-btn ${prefs.theme === t ? 'on' : ''}`} onClick={() => setPref({ theme: t })}>{t[0].toUpperCase() + t.slice(1)}</button>
            ))}
          </div>
        </div>
        <div className="pref-row">
          <div className="pref-text"><div className="pref-label">Reduce motion</div><div className="hint">Minimise animations and transitions.</div></div>
          <button className={`toggle ${prefs.reduceMotion ? 'on' : ''}`} role="switch" aria-checked={prefs.reduceMotion} onClick={() => setPref({ reduceMotion: !prefs.reduceMotion })}><span className="knob" /></button>
        </div>
      </section>

      <section className="card">
        <div className="row-head"><h2>Your save</h2><button className="primary" disabled={busy} onClick={backup}>Back up now</button></div>
                {staged && <div className="banner ok">Restore staged — <b>restart Callsign</b> to load it.</div>}
        <p className="hint">A backup is a full, self-contained copy of your career. Take one before a big change; download it to keep it safe off your PC, or restore it any time.</p>
        {backups.length === 0
          ? <div className="empty">No backups yet. Take one with “Back up now”.</div>
          : (
            <div className="tbl-wrap"><table className="tbl">
              <thead><tr><th>Backup</th><th className="r">Size</th><th className="r">Taken</th><th></th></tr></thead>
              <tbody>{backups.map(b => (
                <tr key={b.name}>
                  <td className="num">{b.name}</td>
                  <td className="r num muted">{kb(b.sizeBytes)}</td>
                  <td className="r muted">{when(b.createdUtc)}</td>
                  <td className="r"><span className="rowacts">
                    <a className="dl" href={api.backupDownloadUrl(b.name)} download>Download</a>
                    <button disabled={busy} onClick={() => restore(b.name)}>Restore</button>
                  </span></td>
                </tr>
              ))}</tbody>
            </table></div>
          )}
      </section>

      <section className="card">
        <h2>About</h2>
        <p className="about-line">Callsign{ver ? <> · <span className="num">v{ver.version}</span></> : ''} — a career &amp; economy companion for Microsoft Flight Simulator 2024.</p>
        <p className="hint">Your save and its <b>backups</b> folder live in <span className="num">%LOCALAPPDATA%\Callsign</span>. Every dollar runs through the ledger, so the Logbook always reconciles with your cash.</p>
      </section>
    </div>
  )
}

// ─── helpers ─────────────────────────────────────────────────────────────────

function signed(n: number): string { return n > 0 ? `+${n.toLocaleString()}` : n.toLocaleString() }
function kb(bytes: number): string {
  return bytes >= 1024 * 1024 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
}
function spaced(s: string): string { return s.replace(/([a-z])([A-Z])/g, '$1 $2') }
function isPaxType(type: string): boolean { return type === 'Passenger' || type === 'Vip' || type === 'Tourist' }
// A job's "load" reads as seats for passenger charters, freight weight for cargo.
function loadText(type: string, weightLbs: number, pax: number): string {
  return isPaxType(type) ? `${pax} pax` : `${weightLbs.toLocaleString()} lb`
}
function when(iso: string): string {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}
function landingWord(fpm: number): string {
  const f = Math.abs(fpm)
  if (f <= 60) return 'butter'
  if (f <= 180) return 'smooth'
  if (f <= 360) return 'firm'
  if (f <= 600) return 'hard'
  return 'rough'
}
function cleanErr(e: unknown): string {
  const s = String(e)
  const m = s.match(/"error":"([^"]+)"/) // pull the server's message out of a failed fetch
  return m ? m[1] : s.replace(/^Error:\s*/, '')
}
