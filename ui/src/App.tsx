import { useCallback, useEffect, useRef, useState } from 'react'
import {
  api, money,
  type AircraftOffer, type Assignment, type BaseOffer, type BaseView, type CheckFlightDone, type Diverted,
  type FinancesData, type FlightLog, type Inventory, type Job, type LedgerEntry, type Loan, type LoanOffer, type Loans,
  type MarketQuote, type OwnedAircraft, type QualClass, type RankTier, type ReconcileResult, type Reputation, type Settled,
  type Staff, type StaffCandidate, type StandingOrder, type State, type Telemetry, type WsEvent,
} from './api'

type Tab = 'dashboard' | 'jobs' | 'flight' | 'hangar' | 'ops' | 'bases' | 'trade' | 'finances' | 'logbook'

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
        {tab === 'hangar' && <Hangar state={state} onChanged={reload} />}
        {tab === 'ops' && <Ops onChanged={reload} />}
        {tab === 'bases' && <Bases state={state} onChanged={reload} />}
        {tab === 'trade' && <Trade state={state} onChanged={reload} />}
        {tab === 'finances' && <Finances state={state} onChanged={reload} />}
        {tab === 'logbook' && <Logbook />}
      </main>
    </div>
  )
}

// ─── Shell ───────────────────────────────────────────────────────────────────

function TopBar({ state, tab, setTab }: { state: State; tab: Tab; setTab: (t: Tab) => void }) {
  const tabs: [Tab, string][] = [
    ['dashboard', 'Dashboard'], ['jobs', 'Jobs'], ['flight', 'Flight'], ['hangar', 'Hangar'], ['ops', 'Staff'], ['bases', 'Bases'], ['trade', 'Trade'], ['finances', 'Finances'], ['logbook', 'Logbook'],
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
                        <td>{a.name} <span className="muted">· {spaced(a.category)}</span></td>
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

  const load = useCallback(async () => {
    try {
      setStaff(await api.staff())
      setCandidates(await api.staffCandidates())
      setOrders(await api.orders())
      setFleet((await api.hangar()).filter(h => h.availability === 'Available'))
      const uniq = new Map((await api.jobs()).map(j => [j.dest, j.destName]))
      setDests(Array.from(uniq, ([icao, name]) => ({ icao, name })))
    } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

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
    </div>
  )
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

  return (
    <div className="grid">
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
  const [amount, setAmount] = useState(50000) // dollars
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setData(await api.loans()); setFin(await api.finances()) } catch (e) { setMsg(cleanErr(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

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

// ─── helpers ─────────────────────────────────────────────────────────────────

function signed(n: number): string { return n > 0 ? `+${n.toLocaleString()}` : n.toLocaleString() }
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
