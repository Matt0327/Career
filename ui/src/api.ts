// Typed client for the Callsign Host API. Money is always integer cents on the wire;
// format for display with money() so the UI never does lossy float math.

export interface State {
  name: string
  rank: string
  xp: number
  currentIcao: string
  homeIcao: string
  cashCents: number
  cash: number
  flights: number
}

export interface Job {
  id: string
  type: string
  origin: string
  dest: string
  destName: string
  commodity: string
  weightLbs: number
  distanceNm: number
  rewardCents: number
  xp: number
  expiresAt: string
}

export interface Assignment {
  id: string
  origin: string
  dest: string
  destName: string
  commodity: string
  weightLbs: number
  distanceNm: number
  rewardQuoteCents: number
  xpQuote: number
  status: string
}

export interface LedgerEntry {
  at: string
  category: string
  amountCents: number
  description: string
}

export interface FlightLog {
  id: string
  aircraftTitle: string
  touchdownFpm: number
  payoutCents: number
  xp: number
  settledAt: string
}

/** A live telemetry frame pushed over the WebSocket. */
export interface Telemetry {
  type: 'telemetry'
  phase: string
  connection: string
  alt: number
  ias: number
  gs: number
  vs: number
  onGround: boolean
  lat: number
  lon: number
  fuel: number
  title: string
}

/** A settlement event pushed over the WebSocket when a begun flight lands. */
export interface Settled {
  type: 'settled'
  assignmentId: string
  payoutCents: number
  xp: number
  payloadMatched: boolean
  touchdownFpm: number
}

/** A link-state change (Connecting / Connected / Disconnected / SimExited), pushed even when no
 *  telemetry frames are flowing — the live SimConnect source is silent until the sim is up. */
export interface LinkState {
  type: 'state'
  connection: string
  phase: string
}

/** Landed away from the destination — the job stays open so you can fly on. */
export interface Diverted {
  type: 'diverted'
  assignmentId: string
  destIcao: string
  distanceNm: number
}

export type WsEvent = Telemetry | Settled | LinkState | Diverted

async function ok<T>(r: Response): Promise<T> {
  if (!r.ok) throw new Error(`${r.status} ${r.statusText}: ${await r.text()}`)
  return r.json() as Promise<T>
}

const POST = (url: string, body?: unknown): Promise<Response> =>
  fetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

export const api = {
  /** Current career, or null if none has been started yet. */
  async state(): Promise<State | null> {
    const r = await fetch('/api/game/state')
    if (r.status === 404) return null
    return ok<State>(r)
  },
  newCareer: (name: string, homeIcao: string, startingCash: number) =>
    POST('/api/game/new', { name, homeIcao, startingCash }).then(ok),
  refreshJobs: (count = 8) => POST(`/api/jobs/refresh?count=${count}`).then(ok),
  jobs: () => fetch('/api/jobs').then(ok<Job[]>),
  accept: (id: string) => POST(`/api/jobs/${id}/accept`).then(ok),
  assignments: () => fetch('/api/assignments').then(ok<Assignment[]>),
  beginFlight: (assignmentId: string) => POST('/api/flight/begin', { assignmentId }).then(ok),
  ledger: (limit = 50) => fetch(`/api/ledger?limit=${limit}`).then(ok<LedgerEntry[]>),
  flights: () => fetch('/api/flights').then(ok<FlightLog[]>),
}

/** Whole-dollar, sign-aware money from integer cents: 147000 -> "$1,470". */
export function money(cents: number): string {
  const dollars = cents / 100
  const sign = dollars < 0 ? '-' : ''
  return `${sign}$${Math.abs(dollars).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}
