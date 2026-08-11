import { useCallback, useEffect, useRef, useState } from 'react'
import {
  api, money,
  type Assignment, type Diverted, type FlightLog, type Job, type LedgerEntry, type Settled, type State, type Telemetry, type WsEvent,
} from './api'

type Tab = 'dashboard' | 'jobs' | 'flight' | 'logbook'

export function App() {
  const [state, setState] = useState<State | null | undefined>(undefined) // undefined = still loading
  const [tab, setTab] = useState<Tab>('dashboard')
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    try {
      setState(await api.state())
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  if (state === undefined) return <Splash />
  if (state === null) return <NewCareer onStarted={reload} />

  return (
    <div className="app">
      <TopBar state={state} tab={tab} setTab={setTab} />
      <main className="main">
        {error && <div className="banner error" onClick={() => setError(null)}>{error} — tap to dismiss</div>}
        {tab === 'dashboard' && <Dashboard state={state} go={setTab} />}
        {tab === 'jobs' && <Jobs state={state} onChanged={reload} />}
        {tab === 'flight' && <Flight state={state} onSettled={reload} />}
        {tab === 'logbook' && <Logbook />}
      </main>
    </div>
  )
}

// ─── Shell ───────────────────────────────────────────────────────────────────

function TopBar({ state, tab, setTab }: { state: State; tab: Tab; setTab: (t: Tab) => void }) {
  const tabs: [Tab, string][] = [
    ['dashboard', 'Dashboard'], ['jobs', 'Jobs'], ['flight', 'Flight'], ['logbook', 'Logbook'],
  ]
  return (
    <header className="topbar">
      <div className="brand"><span className="mark">◄</span> CALLSIGN</div>
      <nav className="nav">
        {tabs.map(([id, label]) => (
          <button key={id} className={`pill ${tab === id ? 'on' : ''}`} onClick={() => setTab(id)}>{label}</button>
        ))}
      </nav>
      <div className="who">
        <div className="who-main">{state.name} · <span className="muted">{state.rank}</span></div>
        <div className="who-sub"><span className="loc">{state.currentIcao}</span> · {state.xp.toLocaleString()} XP</div>
      </div>
      <div className="cash" title="Company cash">{money(state.cashCents)}</div>
    </header>
  )
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
  useEffect(() => { api.assignments().then(setAssignments).catch(() => {}) }, [])

  return (
    <div className="grid">
      <section className="stats">
        <Stat label="Cash" value={money(state.cashCents)} big />
        <Stat label="Experience" value={`${state.xp.toLocaleString()} XP`} />
        <Stat label="Rank" value={state.rank} />
        <Stat label="Flights flown" value={String(state.flights)} />
        <Stat label="Location" value={state.currentIcao} />
      </section>

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
                  <span>{a.weightLbs.toLocaleString()} lb</span>
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
                <div className="card job" key={j.id}>
                  <div className="job-top">
                    <div className="leg"><b>{j.origin}</b> → <b>{j.dest}</b></div>
                    <div className="tag">{j.type}</div>
                  </div>
                  <div className="dest-name">{j.destName}</div>
                  <div className="commodity">{j.commodity}</div>
                  <div className="job-meta">
                    <Meta label="Distance" value={`${Math.round(j.distanceNm)} nm`} />
                    <Meta label="Payload" value={`${j.weightLbs.toLocaleString()} lb`} />
                    <Meta label="XP" value={`+${j.xp}`} />
                  </div>
                  <div className="job-foot">
                    <div className="reward num">{money(j.rewardCents)}</div>
                    <button className="primary" disabled={busy} onClick={() => accept(j.id)}>Accept</button>
                  </div>
                </div>
              ))}
            </div>
          )}
    </div>
  )
}

function Meta({ label, value }: { label: string; value: string }) {
  return <div className="metacell"><span className="metalabel">{label}</span><span className="num">{value}</span></div>
}

// ─── Flight (live HUD + settlement) ──────────────────────────────────────────

function useTelemetry(onSettled: (s: Settled) => void, onDiverted: (d: Diverted) => void) {
  const [tele, setTele] = useState<Telemetry | null>(null)
  const [wsOpen, setWsOpen] = useState(false)
  const [link, setLink] = useState('Disconnected') // SimConnectionState from the server
  const cb = useRef(onSettled)
  cb.current = onSettled
  const dcb = useRef(onDiverted)
  dcb.current = onDiverted

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

  const loadAssignments = useCallback(() => { api.assignments().then(setAssignments).catch(() => {}) }, [])
  useEffect(() => { loadAssignments() }, [loadAssignments])

  const { tele, wsOpen, link } = useTelemetry(
    s => {
      setSettled(s)
      setDiverted(null)
      setBegun(null)
      onSettled()
      loadAssignments()
    },
    d => setDiverted(d), // landed away from the destination — the job stays open
  )
  const badge = linkBadge(wsOpen, link)

  const begin = async (a: Assignment) => {
    setSettled(null)
    setDiverted(null)
    await api.beginFlight(a.id)
    setBegun(a)
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

      <section className="card">
        <h2>Ready to fly</h2>
        {assignments.length === 0
          ? <div className="empty"><p>No accepted jobs. Accept one on the Jobs board first.</p></div>
          : (
            <ul className="assign-list">
              {assignments.map(a => (
                <li key={a.id} className="assign">
                  <div className="leg"><b>{a.origin}</b> → <b>{a.dest}</b> <span className="muted">{a.destName} · {a.commodity}</span></div>
                  <div className="assign-meta">
                    <span>{Math.round(a.distanceNm)} nm</span>
                    <span className="pos">{money(a.rewardQuoteCents)}</span>
                  </div>
                  <button className="primary" disabled={begun?.id === a.id} onClick={() => begin(a)}>
                    {begun?.id === a.id ? 'In progress…' : 'Begin flight'}
                  </button>
                </li>
              ))}
            </ul>
          )}
        <p className="hint muted">Signed in as {state.name} · flying out of {state.currentIcao}.</p>
      </section>
    </div>
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
        {settled.payloadMatched && <span className="pos">payload bonus</span>}
      </div>
    </section>
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

// ─── helpers ─────────────────────────────────────────────────────────────────

function signed(n: number): string { return n > 0 ? `+${n.toLocaleString()}` : n.toLocaleString() }
function spaced(s: string): string { return s.replace(/([a-z])([A-Z])/g, '$1 $2') }
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
