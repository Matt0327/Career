import { useCallback, useEffect, useRef, useState } from 'react'
import {
  api, money,
  type Achievement, type AircraftOffer, type AirlineData, type Assignment, type BackupFile, type BaseOffer, type BaseView, type Campaign, type CheckFlightDone, type Diverted,
  type FinancesData, type FlightLog, type Insurance, type Inventory, type Job, type LedgerEntry, type Loan, type LoanOffer, type Loans,
  type MarketQuote, type OwnedAircraft, type QualClass, type RankTier, type ReconcileResult, type Reputation,
  type RouteData, type Settled, type Staff, type StaffCandidate, type StandingOrder, type State, type Telemetry, type VersionInfo, type WsEvent,
} from './api'
import { loadPrefs, savePrefs, type Prefs, type Theme } from './prefs'
import * as L from 'leaflet'
import 'leaflet/dist/leaflet.css'

type Tab = 'dashboard' | 'airline' | 'jobs' | 'flight' | 'hangar' | 'ops' | 'bases' | 'trade' | 'finances' | 'campaigns' | 'awards' | 'logbook' | 'settings'

export function App() {
  const [state, setState] = useState<State | null | undefined>(undefined) // undefined = still loading
  const [tab, setTab] = useState<Tab>('dashboard')
  const [error, setError] = useState<string | null>(null)
  const [airline, setAirline] = useState<AirlineData | null>(null)

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

  if (state === undefined) return <Splash />
  if (state === null) return <NewCareer onStarted={reload} />

  return (
    <div className="app">
      <NavRail tab={tab} setTab={setTab} airline={airline} />
      <div className="work">
        <ContextHeader state={state} tab={tab} />
        <main className="main">
        {error && <div className="banner error" onClick={() => setError(null)}>{error} — tap to dismiss</div>}
        {tab === 'dashboard' && <Dashboard state={state} go={setTab} />}
        {tab === 'airline' && <Airline onSaved={() => { void reload(); loadAirline() }} />}
        {tab === 'jobs' && <Jobs state={state} onChanged={reload} />}
        {tab === 'flight' && <Flight state={state} onSettled={reload} />}
        {tab === 'hangar' && <Hangar state={state} onChanged={reload} />}
        {tab === 'ops' && <Ops onChanged={reload} />}
        {tab === 'bases' && <Bases state={state} onChanged={reload} />}
        {tab === 'trade' && <Trade state={state} onChanged={reload} />}
        {tab === 'finances' && <Finances state={state} onChanged={reload} />}
        {tab === 'campaigns' && <Campaigns onChanged={reload} />}
        {tab === 'awards' && <Awards />}
        {tab === 'logbook' && <Logbook />}
        {tab === 'settings' && <Settings />}
        </main>
      </div>
    </div>
  )
}

// ─── Shell: nav rail + context header ────────────────────────────────────────

const TABS: { id: Tab; label: string; sub: string }[] = [
  { id: 'dashboard', label: 'Dashboard', sub: 'Your operation at a glance' },
  { id: 'airline', label: 'Airline', sub: 'Identity & standing' },
  { id: 'jobs', label: 'Jobs', sub: 'Find and accept work' },
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

function NavRail({ tab, setTab, airline }: { tab: Tab; setTab: (t: Tab) => void; airline: AirlineData | null }) {
  const item = (t: Tab, label: string) => (
    <button key={t} className={`ric ${tab === t ? 'on' : ''}`} onClick={() => setTab(t)} aria-label={label}>
      {navIcon(t)}<span className="tip">{label}</span>
    </button>
  )
  return (
    <aside className="rail">
      <button className="rail-emblem" title="Airline identity" onClick={() => setTab('airline')} aria-label="Airline">
        {airline
          ? <Emblem emblem={airline.identity.emblemKey} color={airline.identity.accentColorHex} size={34} />
          : <span className="mark" style={{ fontSize: 24 }}>◄</span>}
      </button>
      {TABS.filter(t => t.id !== 'settings').map(t => item(t.id, t.label))}
      <div style={{ marginTop: 'auto' }}>{item('settings', 'Settings')}</div>
    </aside>
  )
}

function ContextHeader({ state, tab }: { state: State; tab: Tab }) {
  const meta = TABS.find(t => t.id === tab)
  return (
    <header className="ctxbar">
      <div className="ctx-title">
        <h1>{meta?.label ?? 'Callsign'}</h1>
        <div className="sub">{meta?.sub ?? ''}</div>
      </div>
      <div className="ctx">
        <span className="chip"><span className="dot" /> <b className="loc">{state.currentIcao}</b></span>
        <span className="chip">{state.name} · <span className="muted">{state.rank}</span> · {state.xp.toLocaleString()} XP</span>
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

// ─── New career ──────────────────────────────────────────────────────────────

function NewCareer({ onStarted }: { onStarted: () => void }) {
  const [name, setName] = useState('Amelia Hart')
  const [home, setHome] = useState('EHAM')
  const [cash, setCash] = useState(25000)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const start = async () => {
    setBusy(true); setErr(null)
    try {
      await api.newCareer(name.trim() || 'New Pilot', home.trim().toUpperCase() || 'EHAM', cash)
      onStarted()
    } catch (e) {
      setErr(String(e)); setBusy(false)
    }
  }

  return (
    <div className="splash">
      <div className="card new-career">
        <div className="brand big"><span className="mark">◄</span> CALLSIGN</div>
        <p className="muted">Start a new career. You fly the aircraft you already own in the sim — Callsign scans them for you.</p>
        <label>Pilot name<input value={name} onChange={e => setName(e.target.value)} /></label>
        <label>Home base (ICAO)<input value={home} onChange={e => setHome(e.target.value)} maxLength={4} /></label>
        <label>Starting cash
          <input type="number" value={cash} min={0} step={1000} onChange={e => setCash(Number(e.target.value))} />
        </label>
        {err && <div className="banner error">{err}</div>}
        <button className="primary" disabled={busy} onClick={start}>{busy ? 'Setting up…' : 'Start career'}</button>
        <p className="hint">First run imports a public-domain airport database — it can take a minute.</p>
      </div>
    </div>
  )
}

// ─── Dashboard ───────────────────────────────────────────────────────────────

function Dashboard({ state, go }: { state: State; go: (t: Tab) => void }) {
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [ranks, setRanks] = useState<RankTier[]>([])
  const [rep, setRep] = useState<Reputation | null>(null)
  useEffect(() => {
    api.assignments().then(setAssignments).catch(() => {})
    api.ranks().then(setRanks).catch(() => {})
    api.reputation().then(setRep).catch(() => {})
  }, [])

  return (
    <div className="grid">
      <section className="stats">
        <Stat label="Cash" value={money(state.cashCents)} big />
        <Stat label="Experience" value={`${state.xp.toLocaleString()} XP`} />
        <Stat label="Rank" value={state.rank} />
        <Stat label="Reputation" value={(state.reputationMilli / 1000).toFixed(1)} />
        <Stat label="Flights flown" value={String(state.flights)} />
        <Stat label="Location" value={state.currentIcao} />
      </section>

      {ranks.length > 0 && <RankCard state={state} ranks={ranks} />}
      {rep && rep.events.length > 0 && <ReputationCard rep={rep} />}

      <section className="card">
        <h2>Active assignment</h2>
        {assignments.length === 0 ? (
          <div className="empty">
            <p>No job accepted yet.</p>
            <button className="primary" onClick={() => go('jobs')}>Browse jobs</button>
          </div>
        ) : (
          <ul className="assign-list">
            {assignments.map(a => (
              <li key={a.id} className="assign">
                <div className="leg"><b>{a.origin}</b> → <b>{a.dest}</b> <span className="muted">{a.destName} · {a.commodity}</span></div>
                <div className="assign-meta">
                  <span>{Math.round(a.distanceNm)} nm</span>
                  <span>{loadText(a.type, a.weightLbs, a.pax)}</span>
                  <span className="pos">{money(a.rewardQuoteCents)}</span>
                </div>
                <button className="primary" onClick={() => go('flight')}>Go to flight →</button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="card how">
        <h2>How a leg works</h2>
        <ol>
          <li>Pick a job on the <b>Jobs</b> board — the reward is quoted and locked when you accept.</li>
          <li>Open <b>Flight</b>, begin the leg, and fly it in the sim.</li>
          <li>Land at the destination — Callsign settles the job automatically and pays you, itemized.</li>
        </ol>
        <p className="hint">Every dollar moves through the ledger, so the <b>Logbook</b> always reconciles with your cash.</p>
      </section>
    </div>
  )
}

function Stat({ label, value, big }: { label: string; value: string; big?: boolean }) {
  return (
    <div className={`stat ${big ? 'stat-big' : ''}`}>
      <div className="stat-label">{label}</div>
      <div className="stat-value num">{value}</div>
    </div>
  )
}

// ─── Jobs ────────────────────────────────────────────────────────────────────

function Jobs({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [jobs, setJobs] = useState<Job[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setJobs(await api.jobs()) } catch (e) { setMsg(String(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const refresh = async () => {
    setBusy(true); setMsg(null)
    try { await api.refreshJobs(8); await load() } catch (e) { setMsg(String(e)) } finally { setBusy(false) }
  }

  const accept = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.accept(id); await load(); onChanged(); setMsg('Accepted — head to the Flight tab to fly it.') }
    catch (e) { setMsg(String(e)) } finally { setBusy(false) }
  }

  return (
    <div>
      <div className="row-head">
        <h2>Jobs from <span className="loc">{state.currentIcao}</span></h2>
        <button className="primary" disabled={busy} onClick={refresh}>{busy ? '…' : 'Refresh board'}</button>
      </div>
      {msg && <div className="banner">{msg}</div>}
      {jobs === null ? <div className="empty">Loading…</div>
        : jobs.length === 0 ? <div className="empty"><p>No jobs on the board.</p><button className="primary" onClick={refresh}>Generate jobs</button></div>
          : (
            <div className="jobs">
              {jobs.map(j => (
                <div className={`card job ${j.locked ? 'locked' : ''}`} key={j.id}>
                  <div className="job-top">
                    <div className="leg"><b>{j.origin}</b> → <b>{j.dest}</b></div>
                    <div className="tag">{j.type}</div>
                  </div>
                  <div className="dest-name">{j.destName}</div>
                  <div className="commodity">{j.commodity}</div>
                  <div className="job-meta">
                    <Meta label="Distance" value={`${Math.round(j.distanceNm)} nm`} />
                    <Meta label={isPaxType(j.type) ? 'Passengers' : 'Payload'} value={loadText(j.type, j.weightLbs, j.pax)} />
                    <Meta label="XP" value={`+${j.xp}`} />
                  </div>
                  <div className="job-foot">
                    <div className="reward num">{money(j.rewardCents)}</div>
                    {j.locked
                      ? <span className="lock" title={j.lockReason ?? ''}>🔒 {j.lockReason}</span>
                      : <button className="primary" disabled={busy} onClick={() => accept(j.id)}>Accept</button>}
                  </div>
                </div>
              ))}
            </div>
          )}
    </div>
  )
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

function useTelemetry(onSettled: (s: Settled) => void, onDiverted: (d: Diverted) => void, onCheckFlight: (c: CheckFlightDone) => void) {
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [wsOpen, setWsOpen] = useState(false)
  const [link, setLink] = useState('Disconnected') // SimConnectionState from the server
  const cb = useRef(onSettled)
  cb.current = onSettled
  const dcb = useRef(onDiverted)
  dcb.current = onDiverted
  const ccb = useRef(onCheckFlight)
  ccb.current = onCheckFlight

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

function Flight({ state, onSettled }: { state: State; onSettled: () => void }) {
  const [assignments, setAssignments] = useState<Assignment[]>([])
  const [begun, setBegun] = useState<Assignment | null>(null)
  const [settled, setSettled] = useState<Settled | null>(null)
  const [diverted, setDiverted] = useState<Diverted | null>(null)
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [aircraftId, setAircraftId] = useState('')
  const [beginErr, setBeginErr] = useState<string | null>(null)
  const [quals, setQuals] = useState<QualClass[]>([])
  const [checkPending, setCheckPending] = useState<string | null>(null) // class name of a check-flight in progress
  const [checkResult, setCheckResult] = useState<CheckFlightDone | null>(null)

  const loadAssignments = useCallback(() => { api.assignments().then(setAssignments).catch(() => {}) }, [])
  const loadQuals = useCallback(() => { api.quals().then(setQuals).catch(() => {}) }, [])
  const loadFleet = useCallback(() => {
    api.hangar().then(hs => {
      const avail = hs.filter(h => h.availability === 'Available')
      setFleet(avail)
      // Default to an aircraft you're actually rated to fly (3c), else the first available.
      setAircraftId(prev => prev || avail.find(h => h.rated)?.id || avail[0]?.id || '')
    }).catch(() => {})
  }, [])
  useEffect(() => { loadAssignments(); loadFleet(); loadQuals() }, [loadAssignments, loadFleet, loadQuals])

  const { tele, wsOpen, link } = useTelemetry(
    s => {
      setSettled(s)
      setDiverted(null)
      setBegun(null)
      onSettled()
      loadAssignments()
      loadFleet() // the airframe moved to the destination + ticked hours
    },
    d => setDiverted(d), // landed away from the destination — the job stays open
    c => { // a check-flight was graded on landing (3d)
      setCheckResult(c)
      setCheckPending(null)
      onSettled()      // cash changed (the fee)
      loadQuals()      // a pass adds/upgrades a class
      loadFleet()      // newly-rated aircraft become flyable
    },
  )
  const badge = linkBadge(wsOpen, link)

  const beginCheck = async (cls: string, name: string) => {
    setBeginErr(null); setCheckResult(null)
    try { await api.beginCheckFlight(cls); setCheckPending(name) }
    catch (e) { setBeginErr(cleanErr(e)) }
  }

  const begin = async (a: Assignment) => {
    setSettled(null)
    setDiverted(null)
    setBeginErr(null)
    try {
      await api.beginFlight(a.id, aircraftId || undefined)
      setBegun(a)
    } catch (e) {
      setBeginErr(cleanErr(e)) // e.g. "You're not rated for the …"
    }
  }

  return (
    <div className="grid">
      <section className="card hud">
        <div className="hud-head">
          <h2>Live flight</h2>
          <span className={`conn ${badge.tone}`}>{badge.text}</span>
        </div>
        <div className="phase num">{tele?.phase ?? '—'}</div>
        <div className="gauges">
          <Gauge label="Altitude" value={tele ? Math.round(tele.alt).toLocaleString() : '—'} unit="ft" />
          <Gauge label="Airspeed" value={tele ? Math.round(tele.ias).toString() : '—'} unit="kt IAS" />
          <Gauge label="Vertical" value={tele ? signed(Math.round(tele.vs)) : '—'} unit="fpm"
            tone={tele ? (tele.vs < -50 ? 'down' : tele.vs > 50 ? 'up' : undefined) : undefined} />
          <Gauge label="Ground" value={tele ? (tele.onGround ? 'ON' : 'AIR') : '—'} unit={tele?.title?.split('(')[0].trim() ?? ''} />
        </div>
        {diverted && <div className="banner warn">You landed {Math.round(diverted.distanceNm)} nm from <b>{diverted.destIcao}</b>. The job's still open — take off and fly on to {diverted.destIcao}.</div>}
        {begun
          ? <div className="banner ok">Flying <b>{begun.origin} → {begun.dest}</b> · {begun.destName} — land at {begun.dest} and Callsign settles it automatically.</div>
          : <div className="hint">Begin a leg below, then fly it. The next landing at the destination settles the job.</div>}
      </section>

      {settled && <SettlementCard settled={settled} />}
      {checkPending && <div className="banner ok">Check-flight for <b>{checkPending}</b> in progress — fly a clean landing (≤ 200 fpm) and it grades automatically.</div>}
      {checkResult && <CheckFlightCard result={checkResult} />}

      <section className="card">
        <div className="row-head">
          <h2>Ready to fly</h2>
          {fleet.length > 0
            ? <label className="pick">Aircraft&nbsp;
                <select value={aircraftId} onChange={e => setAircraftId(e.target.value)}>
                  {fleet.map(f => <option key={f.id} value={f.id} disabled={!f.rated}>{f.tail} · {f.name} — {f.locationIcao}{f.rated ? '' : ' · not rated'}</option>)}
                </select>
              </label>
            : <span className="hint">No available aircraft — buy one in the Hangar.</span>}
        </div>
        {beginErr && <div className="banner error" onClick={() => setBeginErr(null)}>{beginErr} — tap to dismiss</div>}
        {assignments.length === 0
          ? <div className="empty"><p>No accepted jobs. Accept one on the Jobs board first.</p></div>
          : (
            <ul className="assign-list">
              {assignments.map(a => (
                <li key={a.id} className="assign">
                  <div className="leg"><b>{a.origin}</b> → <b>{a.dest}</b> <span className="muted">{a.destName} · {a.commodity}</span></div>
                  <div className="assign-meta">
                    <span>{Math.round(a.distanceNm)} nm</span>
                    <span>{loadText(a.type, a.weightLbs, a.pax)}</span>
                    <span className="pos">{money(a.rewardQuoteCents)}</span>
                  </div>
                  <button className="primary" disabled={begun?.id === a.id || !aircraftId} onClick={() => begin(a)}>
                    {begun?.id === a.id ? 'In progress…' : 'Begin flight'}
                  </button>
                </li>
              ))}
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
                  <button className="primary" disabled={checkPending !== null || state.cashCents < q.checkFlightFeeCents}
                          title={state.cashCents < q.checkFlightFeeCents ? 'Not enough cash' : ''}
                          onClick={() => beginCheck(q.class, q.displayName)}>
                    {q.held ? 'Re-test' : 'Begin check-flight'}
                  </button>
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

function Gauge({ label, value, unit, tone }: { label: string; value: string; unit: string; tone?: 'up' | 'down' }) {
  return (
    <div className="gauge">
      <div className="gauge-label">{label}</div>
      <div className={`gauge-value num ${tone ?? ''}`}>{value}</div>
      <div className="gauge-unit">{unit}</div>
    </div>
  )
}

function SettlementCard({ settled }: { settled: Settled }) {
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
    </section>
  )
}

// ─── Aircraft imagery (Phase 6b) ─────────────────────────────────────────────

// The aircraft's own thumbnail from your installed sim; falls back to an original category silhouette
// (so it looks intentional on a machine with no MSFS, or for an aircraft that ships no thumbnail).
function AircraftImage({ typeId, category, mini }: { typeId?: string; category?: string; mini?: boolean }) {
  const [failed, setFailed] = useState(false)
  useEffect(() => { setFailed(false) }, [typeId])
  return (
    <div className={`ac-img ${mini ? 'mini' : ''}`}>
      {typeId && !failed
        ? <img src={api.aircraftThumbUrl(typeId)} alt="" loading="lazy" onError={() => setFailed(true)} />
        : <AircraftSilhouette category={category} />}
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

// ─── Hangar (own aircraft) ───────────────────────────────────────────────────

function Hangar({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [owned, setOwned] = useState<OwnedAircraft[] | null>(null)
  const [offers, setOffers] = useState<AircraftOffer[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setOwned(await api.hangar()); setOffers(await api.market()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const buy = async (o: AircraftOffer) => {
    setBusy(true); setMsg(null)
    try { await api.buyAircraft(o.typeId); await load(); onChanged(); setMsg(`Bought a ${o.name} — it's in your hangar.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const maintain = async (a: OwnedAircraft) => {
    setBusy(true); setMsg(null)
    try { await api.maintain(a.id); await load(); onChanged(); setMsg(`Serviced ${a.tail} — good as new.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      <section className="card">
        <h2>Your hangar</h2>
        {owned === null ? <div className="empty">Loading…</div>
          : owned.length === 0 ? <div className="empty">No aircraft yet — buy one below.</div>
            : (
              <table className="tbl">
                <thead><tr><th>Tail</th><th>Aircraft</th><th>At</th><th className="r">Hours</th><th className="r">Condition</th><th>Rating</th><th className="r">Maintenance</th></tr></thead>
                <tbody>
                  {owned.map(a => {
                    const cond = Math.round(Math.min(a.hullConditionMilli, a.engineConditionMilli) / 1000)
                    return (
                      <tr key={a.id}>
                        <td className="num">{a.tail}</td>
                        <td><div className="ac-cell"><AircraftImage typeId={a.typeId} category={a.category} mini /><span>{a.name} <span className="muted">· {spaced(a.category)}</span></span></div></td>
                        <td className="loc">{a.locationIcao}</td>
                        <td className="r num">{a.airframeHours.toFixed(1)}</td>
                        <td className={`r num ${cond < 40 ? 'neg' : cond < 70 ? '' : 'pos'}`}>{cond}%</td>
                        <td>{a.rated ? <span className="pos">rated</span> : <span className="lock" title={`Needs ${a.requiredClass}`}>🔒 not rated</span>}</td>
                        <td className="r">
                          {a.maintenanceDue
                            ? <button className="primary small" disabled={busy} onClick={() => maintain(a)}>Service · {money(a.maintenanceQuoteCents)}</button>
                            : <span className="muted num">{money(a.maintenanceQuoteCents)}</span>}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            )}
      </section>

      <section className="card">
        <div className="row-head"><h2>Buy an aircraft</h2><span className="hint">Delivered to <span className="loc">{state.currentIcao}</span></span></div>
        {msg && <div className="banner">{msg}</div>}
        {offers === null ? <div className="empty">Loading…</div>
          : offers.length === 0 ? <div className="empty">No aircraft types known yet.</div>
            : (
              <div className="jobs">
                {offers.map(o => {
                  const afford = state.cashCents >= o.priceCents
                  return (
                    <div className="card job" key={o.typeId}>
                      <AircraftImage typeId={o.typeId} category={o.category} />
                      <div className="job-top">
                        <div className="leg"><b>{o.name}</b></div>
                        {o.onDisk && <div className="tag">installed</div>}
                      </div>
                      <div className="commodity">{spaced(o.category)}</div>
                      <div className="job-meta">
                        {o.seats != null && <Meta label="Seats" value={String(o.seats)} />}
                        {o.usefulLoadLbs != null && <Meta label="Payload" value={`${o.usefulLoadLbs.toLocaleString()} lb`} />}
                        {o.cruiseKtas != null && <Meta label="Cruise" value={`${o.cruiseKtas} kt`} />}
                      </div>
                      <div className="price num">{money(o.priceCents)}</div>
                      <details className="factors">
                        <summary>why this price</summary>
                        <ul>{o.factors.map((f, i) => <li key={i}><span>{f.label}</span><span className="num">{money(f.amountCents)}</span></li>)}</ul>
                      </details>
                      <div className="job-foot">
                        <span className="hint">{afford ? '' : 'over budget'}</span>
                        <button className="primary" disabled={busy || !afford} onClick={() => buy(o)}>Buy</button>
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

// ─── Staff & standing orders ─────────────────────────────────────────────────

function Ops({ onChanged }: { onChanged: () => void }) {
  const [staff, setStaff] = useState<Staff[]>([])
  const [candidates, setCandidates] = useState<StaffCandidate[]>([])
  const [orders, setOrders] = useState<StandingOrder[]>([])
  const [fleet, setFleet] = useState<OwnedAircraft[]>([])
  const [dests, setDests] = useState<{ icao: string; name: string }[]>([])
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const [oStaff, setOStaff] = useState('')
  const [oAircraft, setOAircraft] = useState('')
  const [oDest, setODest] = useState('')
  const [routes, setRoutes] = useState<RouteData | null>(null)
  const [rName, setRName] = useState('')
  const [rOrigin, setROrigin] = useState('')
  const [rDest, setRDest] = useState('')
  const [rStaff, setRStaff] = useState('')
  const [rAircraft, setRAircraft] = useState('')
  const [rMission, setRMission] = useState('Cargo')

  const load = useCallback(async () => {
    try {
      setStaff(await api.staff())
      setCandidates(await api.staffCandidates())
      setOrders(await api.orders())
      setFleet((await api.hangar()).filter(h => h.availability === 'Available'))
      const uniq = new Map((await api.jobs()).map(j => [j.dest, j.destName]))
      setDests(Array.from(uniq, ([icao, name]) => ({ icao, name })))
      setRoutes(await api.routes())
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const createRoute = async () => {
    if (!rStaff || !rAircraft || !rOrigin || !rDest) { setMsg('A route needs a pilot, an aircraft, and two of your bases.'); return }
    setBusy(true); setMsg(null)
    try {
      await api.createRoute({ name: rName.trim() || undefined, originIcao: rOrigin, destIcao: rDest, aircraftInstanceId: rAircraft, staffId: rStaff, mission: rMission })
      setRName(''); setROrigin(''); setRDest(''); setRStaff(''); setRAircraft(''); await load(); onChanged(); setMsg('Route opened — it earns while you fly.')
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
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
  const createOrder = async () => {
    if (!oStaff || !oAircraft || !oDest) { setMsg('Pick a pilot, an aircraft, and a destination.'); return }
    setBusy(true); setMsg(null)
    try { await api.createOrder(oStaff, oAircraft, oDest); setOStaff(''); setOAircraft(''); setODest(''); await load(); onChanged(); setMsg("Standing order set — it flies while you're away.") }
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
      setMsg(d.trips > 0 || d.wagesCents > 0 || d.rentCents > 0
        ? `Booked ${d.trips} trip${d.trips === 1 ? '' : 's'}: ${money(d.grossIncomeCents)} gross − ${money(d.feesCents)} fees − ${money(d.wagesCents)} wages − ${money(d.rentCents)} rent = ${money(d.netCents)} net.`
        : 'Up to date — nothing new.')
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      <section className="card">
        <div className="row-head"><h2>Standing orders</h2><button className="primary" disabled={busy} onClick={process}>Process now</button></div>
        {msg && <div className="banner">{msg}</div>}
        {orders.length === 0
          ? <div className="empty">No standing orders. Set one below to earn while you're away.</div>
          : (
            <table className="tbl">
              <thead><tr><th>Pilot</th><th>Aircraft</th><th>Route</th><th className="r">Per trip</th><th className="r">Cycle</th><th></th></tr></thead>
              <tbody>
                {orders.map(o => (
                  <tr key={o.id}>
                    <td>{o.staffName}</td>
                    <td className="num">{o.tail}</td>
                    <td><b>{o.origin}</b> ↔ <b>{o.dest}</b> <span className="muted">· {Math.round(o.distanceNm)} nm</span></td>
                    <td className="r num pos">{money(o.rewardPerTripCents)}</td>
                    <td className="r num">{o.roundTripHours.toFixed(1)} h</td>
                    <td className="r"><button className="primary small" disabled={busy} onClick={() => cancel(o)}>Stop</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        {staff.length > 0 && fleet.length > 0 && dests.length > 0 && (
          <div className="order-form">
            <select value={oStaff} onChange={e => setOStaff(e.target.value)}><option value="">Pilot…</option>{staff.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
            <select value={oAircraft} onChange={e => setOAircraft(e.target.value)}><option value="">Aircraft…</option>{fleet.map(f => <option key={f.id} value={f.id}>{f.tail} — {f.locationIcao}</option>)}</select>
            <select value={oDest} onChange={e => setODest(e.target.value)}><option value="">Destination…</option>{dests.map(d => <option key={d.icao} value={d.icao}>{d.icao} · {d.name}</option>)}</select>
            <button className="primary" disabled={busy} onClick={createOrder}>Set order</button>
          </div>
        )}
      </section>

      <section className="card">
        <h2>Your crew</h2>
        {staff.length === 0 ? <div className="empty">No pilots hired yet.</div> : (
          <table className="tbl">
            <thead><tr><th>Name</th><th className="r">Skill</th><th className="r">Wage / day</th></tr></thead>
            <tbody>{staff.map(s => (
              <tr key={s.id}><td>{s.name}</td><td className="r num">{Math.round(s.skillMilli / 1000)}%</td><td className="r num neg">{money(s.wagePerDayCents)}</td></tr>
            ))}</tbody>
          </table>
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
        <div className="row-head"><h2>Routes</h2><span className="hint">Base-to-base lines — fee-free, earning while you fly</span></div>
        {routes && routes.routes.length > 0 && (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Route</th><th>Leg</th><th className="r">Reward/trip</th><th></th></tr></thead>
            <tbody>{routes.routes.map(r => (
              <tr key={r.id}>
                <td>{r.name} <span className="muted">· {r.mission}</span></td>
                <td><span className="loc">{r.origin}</span> → <span className="loc">{r.dest}</span> <span className="muted">· {Math.round(r.distanceNm)} nm</span></td>
                <td className="r num pos">{money(r.rewardPerTripCents)}</td>
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
              <select value={rStaff} onChange={e => setRStaff(e.target.value)}><option value="">Pilot…</option>{staff.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
              <select value={rAircraft} onChange={e => setRAircraft(e.target.value)}><option value="">Aircraft…</option>{fleet.map(f => <option key={f.id} value={f.id}>{f.tail} — {f.locationIcao}</option>)}</select>
              <select value={rMission} onChange={e => setRMission(e.target.value)}>{routes.missions.map(m => <option key={m} value={m}>{m}</option>)}</select>
              <button className="primary" disabled={busy} onClick={createRoute}>Open route</button>
            </div>
          )}
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
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setBases(await api.bases()); setOffers(await api.baseCandidates()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const open = async (o: BaseOffer) => {
    setBusy(true); setMsg(null)
    try { await api.openBase(o.icao); await load(); onChanged(); setMsg(`Opened a base at ${o.icao} · ${o.name}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const mapPoints: MapPoint[] = [
    ...bases.map((b): MapPoint => ({ lat: b.latitude, lon: b.longitude, label: b.icao, kind: b.isHome ? 'home' : 'base' })),
    ...offers.map((o): MapPoint => ({ lat: o.latitude, lon: o.longitude, label: o.icao, kind: 'field' })),
  ].filter(p => p.lat !== 0 || p.lon !== 0)

  return (
    <div className="grid">
      <section className="card">
        <div className="row-head"><h2>Your network</h2><span className="hint">satellite · {bases.length} base{bases.length === 1 ? '' : 's'}</span></div>
        <SatelliteMap points={mapPoints} />
      </section>
      <section className="card">
        <h2>Your bases</h2>
        {bases.length === 0 ? <div className="empty">No bases.</div> : (
          <table className="tbl">
            <thead><tr><th>Airport</th><th>Name</th><th className="r">Rent / day</th></tr></thead>
            <tbody>{bases.map(b => (
              <tr key={b.id}>
                <td><span className="loc">{b.icao}</span>{b.isHome && <span className="tag" style={{ marginLeft: 8 }}>home</span>}</td>
                <td>{b.name}</td>
                <td className="r num muted">{b.rentPerDayCents ? money(b.rentPerDayCents) : 'free'}</td>
              </tr>
            ))}</tbody>
          </table>
        )}
      </section>

      <section className="card">
        <div className="row-head"><h2>Open a base</h2><span className="hint">Land fee-free at your own bases</span></div>
        {msg && <div className="banner">{msg}</div>}
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
  const [qty, setQty] = useState<Record<string, number>>({})
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setMarket(await api.tradeMarket()); setInv(await api.inventory()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const q = (key: string) => Math.max(1, Math.floor(qty[key] || 1))
  const setQ = (key: string, n: number) => setQty(s => ({ ...s, [key]: n }))

  const buy = async (good: string) => {
    setBusy(true); setMsg(null)
    try { await api.buyGood(good, q('buy-' + good)); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const sell = async (good: string, max: number) => {
    setBusy(true); setMsg(null)
    try {
      const r = await api.sellGood(good, Math.min(q('sell-' + good), max))
      await load(); onChanged()
      const pnl = r.pnlCents >= 0 ? `+${money(r.pnlCents)}` : money(r.pnlCents)
      setMsg(`Sold ${r.quantity} — proceeds ${money(r.proceedsCents)}, P&L ${pnl}.`)
    } catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      <section className="card">
        <div className="row-head"><h2>Market · <span className="loc">{state.currentIcao}</span></h2><span className="hint">Buy low here, fly it, sell high there</span></div>
        {msg && <div className="banner">{msg}</div>}
        <div className="tbl-wrap">
          <table className="tbl">
            <thead><tr><th>Commodity</th><th className="r">Buy</th><th className="r">Sell</th><th className="r">Unit wt</th><th className="r">Qty</th><th></th></tr></thead>
            <tbody>{market.map(m => (
              <tr key={m.good}>
                <td>{m.name}</td>
                <td className="r num">{money(m.buyCents)}</td>
                <td className="r num muted">{money(m.sellCents)}</td>
                <td className="r muted">{m.unitWeightLbs} lb</td>
                <td className="r"><input className="qty" type="number" min={1} value={q('buy-' + m.good)} onChange={e => setQ('buy-' + m.good, Number(e.target.value))} /></td>
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
                return (
                  <tr key={v.id}>
                    <td>{v.name}</td>
                    <td className="r num">{v.quantity}</td>
                    <td className="r num muted">{money(v.unitCostCents)}</td>
                    <td className="r num">{money(v.marketSellCents)}</td>
                    <td className={`r num ${v.unrealizedPnlCents >= 0 ? 'pos' : 'neg'}`}>{money(v.unrealizedPnlCents)}</td>
                    <td><span className="loc">{v.locationIcao}</span></td>
                    <td className="r"><input className="qty" type="number" min={1} max={v.quantity} value={q('sell-' + v.good)} onChange={e => setQ('sell-' + v.good, Number(e.target.value))} /></td>
                    <td className="r"><button disabled={busy || !here} title={here ? '' : `Fly to ${v.locationIcao} to sell`} onClick={() => sell(v.good, v.quantity)}>Sell</button></td>
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

function Finances({ state, onChanged }: { state: State; onChanged: () => void }) {
  const [data, setData] = useState<Loans | null>(null)
  const [fin, setFin] = useState<FinancesData | null>(null)
  const [ins, setIns] = useState<Insurance | null>(null)
  const [amount, setAmount] = useState(50000) // dollars
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setData(await api.loans()); setFin(await api.finances()); setIns(await api.insurance()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  const insure = async (aircraftInstanceId: string) => {
    setBusy(true); setMsg(null)
    try { await api.insure(aircraftInstanceId); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const cancelIns = async (id: string) => {
    setBusy(true); setMsg(null)
    try { await api.cancelInsurance(id); await load(); onChanged() }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const claim = async (id: string) => {
    setBusy(true); setMsg(null)
    try { const r = await api.claimInsurance(id); await load(); onChanged(); setMsg(`Claim paid — ${money(r.paidCents)}. Airframe written off.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  const cents = Math.max(0, Math.round(amount * 100))
  const tier: LoanOffer | undefined = data?.offers.find(o => cents >= o.minPrincipalCents && cents <= o.maxPrincipalCents)

  const take = async () => {
    setBusy(true); setMsg(null)
    try { await api.takeLoan(cents); await load(); onChanged(); setMsg(`Borrowed ${money(cents)} — it's in your cash.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }
  const payoff = async (l: Loan) => {
    setBusy(true); setMsg(null)
    try { const r = await api.payoffLoan(l.id); await load(); onChanged(); setMsg(`Loan cleared — paid ${money(r.paidCents)}.`) }
    catch (e) { setMsg(cleanErr(e)) } finally { setBusy(false) }
  }

  return (
    <div className="grid">
      {fin && (
        <section className="card">
          <div className="row-head"><h2>Net worth</h2><span className={`num rep-score ${fin.netWorth.netWorthCents >= 0 ? 'pos' : 'neg'}`}>{money(fin.netWorth.netWorthCents)}</span></div>
          <table className="tbl">
            <tbody>
              <tr><td>Cash</td><td className="r num">{money(fin.netWorth.cashCents)}</td></tr>
              <tr><td>Aircraft <span className="muted">· resale</span></td><td className="r num">{money(fin.netWorth.aircraftCents)}</td></tr>
              <tr><td>Inventory <span className="muted">· at cost</span></td><td className="r num">{money(fin.netWorth.inventoryCents)}</td></tr>
              <tr><td>Loans <span className="muted">· outstanding</span></td><td className="r num neg">{money(-fin.netWorth.loansCents)}</td></tr>
            </tbody>
          </table>
        </section>
      )}

      {fin && fin.pnl.lines.length > 0 && (
        <section className="card">
          <div className="row-head"><h2>Cash flow · {fin.pnl.days}d</h2><span className={`num rep-score ${fin.pnl.netCents >= 0 ? 'pos' : 'neg'}`}>{money(fin.pnl.netCents)}</span></div>
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

      <section className="card">
        <h2>Your loans</h2>
        {msg && <div className="banner">{msg}</div>}
        {!data ? <div className="empty">Loading…</div>
          : data.loans.length === 0 ? <div className="empty">No loans outstanding. Borrow below to grow faster.</div>
            : (
              <div className="tbl-wrap"><table className="tbl">
                <thead><tr><th>Tier</th><th className="r">Borrowed</th><th className="r">Outstanding</th><th className="r">APR</th><th></th></tr></thead>
                <tbody>{data.loans.map(l => (
                  <tr key={l.id}>
                    <td>{data.offers.find(o => o.tier === l.tier)?.name ?? `Tier ${l.tier}`}</td>
                    <td className="r num muted">{money(l.principalCents)}</td>
                    <td className="r num">{money(l.outstandingCents)}</td>
                    <td className="r num muted">{(l.aprBps / 100).toFixed(1)}%</td>
                    <td className="r"><button disabled={busy || state.cashCents < l.outstandingCents} title={state.cashCents < l.outstandingCents ? 'Not enough cash to clear it' : ''} onClick={() => payoff(l)}>Pay off</button></td>
                  </tr>
                ))}</tbody>
              </table></div>
            )}
      </section>

      <section className="card">
        <div className="row-head"><h2>Borrow</h2><span className="hint">Bigger loans, lower APR</span></div>
        <label className="pick">Amount ($)&nbsp;
          <input type="number" min={0} step={1000} value={amount} onChange={e => setAmount(Number(e.target.value))} />
        </label>
        <p className="muted" style={{ margin: '10px 0' }}>
          {tier
            ? <>Tier <b>{tier.name}</b> at <b>{(tier.aprBps / 100).toFixed(1)}%</b> APR, repaid over 90 days. You have {money(state.cashCents)}.</>
            : 'That amount is outside the lending range.'}
        </p>
        <button className="primary" disabled={busy || !tier} onClick={take}>Borrow {money(cents)}</button>
      </section>

      <section className="card">
        <h2>Lending tiers</h2>
        {data && (
          <div className="tbl-wrap"><table className="tbl">
            <thead><tr><th>Tier</th><th className="r">From</th><th className="r">To</th><th className="r">APR</th></tr></thead>
            <tbody>{data.offers.map(o => (
              <tr key={o.tier}>
                <td>{o.name}</td>
                <td className="r num muted">{money(o.minPrincipalCents)}</td>
                <td className="r num muted">{money(o.maxPrincipalCents)}</td>
                <td className="r num">{(o.aprBps / 100).toFixed(1)}%</td>
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
    </div>
  )
}

// ─── Logbook ─────────────────────────────────────────────────────────────────

function Logbook() {
  const [flights, setFlights] = useState<FlightLog[]>([])
  const [ledger, setLedger] = useState<LedgerEntry[]>([])
  useEffect(() => {
    api.flights().then(setFlights).catch(() => {})
    api.ledger(50).then(setLedger).catch(() => {})
  }, [])

  return (
    <div className="grid">
      <section className="card">
        <h2>Flights</h2>
        {flights.length === 0 ? <div className="empty">No flights logged yet.</div> : (
          <table className="tbl">
            <thead><tr><th>Aircraft</th><th className="r">Touchdown</th><th className="r">Payout</th><th className="r">XP</th><th className="r">When</th></tr></thead>
            <tbody>
              {flights.map(f => (
                <tr key={f.id}>
                  <td>{f.aircraftTitle}</td>
                  <td className="r num">{signed(Math.round(f.touchdownFpm))} <span className="muted">{landingWord(f.touchdownFpm)}</span></td>
                  <td className="r num pos">{money(f.payoutCents)}</td>
                  <td className="r num">+{f.xp}</td>
                  <td className="r muted">{when(f.settledAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section className="card">
        <h2>Ledger</h2>
        {ledger.length === 0 ? <div className="empty">No entries yet.</div> : (
          <table className="tbl">
            <thead><tr><th>Category</th><th>Description</th><th className="r">Amount</th><th className="r">When</th></tr></thead>
            <tbody>
              {ledger.map((e, i) => (
                <tr key={i}>
                  <td>{spaced(e.category)}</td>
                  <td className="muted">{e.description}</td>
                  <td className={`r num ${e.amountCents < 0 ? 'neg' : 'pos'}`}>{money(e.amountCents)}</td>
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

function Airline({ onSaved }: { onSaved: () => void }) {
  const [data, setData] = useState<AirlineData | null>(null)
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [color, setColor] = useState('#4f46e5')
  const [emblem, setEmblem] = useState('roundel')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

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
  const pct = st.nextTierScore ? Math.min(100, (st.score / st.nextTierScore) * 100) : 100

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
        {msg && <div className="banner">{msg}</div>}
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
        <div className="row-head"><h2>Airline standing</h2><span className="tier-badge" style={{ background: `color-mix(in srgb, ${color} 16%, transparent)`, color }}>{st.tierName}</span></div>
        <div className="rank-bar"><div className="rank-fill" style={{ width: `${pct}%`, background: color }} /></div>
        <div className="rank-scale"><span className="num">{st.score} pts</span><span className="num">{st.nextTierScore ? `next tier at ${st.nextTierScore}` : 'top tier'}</span></div>
        {st.contributions.length > 0 && (
          <table className="tbl" style={{ marginTop: 14 }}>
            <tbody>{st.contributions.map(c => (
              <tr key={c.label}><td>{c.label}</td><td className="r num pos">+{c.points}</td></tr>
            ))}</tbody>
          </table>
        )}
        <p className="hint">Standing reads your whole operation — reputation, fleet, network, wealth, and campaigns — into a tier. Computed live, never stored.</p>
      </section>
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

function CampaignCard({ c }: { c: Campaign }) {
  return (
    <section className={`card campaign ${c.completed ? 'done' : ''}`}>
      <div className="row-head">
        <h2>{c.name}</h2>
        {c.completed ? <span className="pill-done">Completed ✓</span> : <span className="hint num">{c.stepIndex} / {c.stepCount}</span>}
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
  const pct = a.target > 0 ? Math.min(100, (a.progress / a.target) * 100) : 0
  return (
    <div className={`ach ${a.earned ? 'earned' : ''}`}>
      <div className="ach-medal">{a.earned ? '★' : '☆'}</div>
      <div className="ach-body">
        <div className="ach-name">{a.name}</div>
        <div className="ach-desc muted">{a.description}</div>
        {a.earned
          ? <div className="ach-when muted">{a.earnedAt ? `Earned ${when(a.earnedAt)}` : 'Earned'}</div>
          : <div className="ach-bar" title={`${a.progress} / ${a.target}`}><div className="ach-fill" style={{ width: `${pct}%` }} /></div>}
      </div>
    </div>
  )
}

// ─── Settings (save backup / restore + about) ────────────────────────────────

function Settings() {
  const [ver, setVer] = useState<VersionInfo | null>(null)
  const [backups, setBackups] = useState<BackupFile[]>([])
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
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
        {msg && <div className="banner">{msg}</div>}
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
