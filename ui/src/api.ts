// Typed client for the Callsign Host API. Money is always integer cents on the wire;
// format for display with money() so the UI never does lossy float math.

export interface State {
  name: string
  rank: string
  xp: number
  reputationMilli: number
  currentIcao: string
  homeIcao: string
  cashCents: number
  cash: number
  flights: number
}

export interface ReputationEvent {
  deltaMilli: number
  balanceMilli: number
  reason: string
  at: string
}

export interface Reputation {
  reputationMilli: number
  events: ReputationEvent[]
}

export interface Job {
  id: string
  type: string
  origin: string
  dest: string
  destName: string
  commodity: string
  weightLbs: number
  pax: number
  distanceNm: number
  rewardCents: number
  xp: number
  requiredRank: string
  locked: boolean
  lockReason?: string | null
  expiresAt: string
}

export interface Assignment {
  id: string
  type: string
  origin: string
  dest: string
  destName: string
  commodity: string
  weightLbs: number
  pax: number
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

export interface PriceFactor {
  label: string
  amountCents: number
}

export interface AircraftOffer {
  typeId: string
  name: string
  category: string
  priceCents: number
  onDisk: boolean
  seats: number | null
  usefulLoadLbs: number | null
  cruiseKtas: number | null
  factors: PriceFactor[]
}

export interface OwnedAircraft {
  id: string
  tail: string
  name: string
  category: string
  locationIcao: string
  availability: string
  purchasePriceCents: number | null
  airframeHours: number
  hullConditionMilli: number
  engineConditionMilli: number
  maintenanceDue: boolean
  maintenanceQuoteCents: number
  requiredClass: string
  rated: boolean
}

export interface QualClass {
  class: string
  displayName: string
  description: string
  held: boolean
  stars: number
  checkFlightFeeCents: number
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
  promotedTo?: string | null
  touchdownFpm: number
}

export interface RankTier {
  rank: string
  displayName: string
  description: string
  minXp: number
  reached: boolean
  current: boolean
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

/** A check-flight was graded on landing (Phase 3d). */
export interface CheckFlightDone {
  type: 'checkflight'
  class: string
  className: string
  passed: boolean
  stars: number
  feeCents: number
  touchdownFpm: number
}

export type WsEvent = Telemetry | Settled | LinkState | Diverted | CheckFlightDone

export interface StaffCandidate {
  seed: number
  name: string
  wagePerDayCents: number
  skillMilli: number
}

export interface Staff {
  id: string
  name: string
  wagePerDayCents: number
  skillMilli: number
}

export interface StandingOrder {
  id: string
  staffName: string
  tail: string
  origin: string
  dest: string
  distanceNm: number
  roundTripHours: number
  rewardPerTripCents: number
}

export interface ReconcileResult {
  trips: number
  grossIncomeCents: number
  feesCents: number
  wagesCents: number
  rentCents: number
  loanCents: number
  insuranceCents: number
  netCents: number
}

export interface InsurancePolicy {
  id: string
  tail: string
  aircraftName: string
  conditionMilli: number
  coverageMilli: number
  premiumPerWeekCents: number
  deductibleCents: number
  claimPayoutCents: number
  claimable: boolean
}

export interface InsuranceQuote {
  aircraftInstanceId: string
  tail: string
  aircraftName: string
  premiumPerWeekCents: number
  deductibleCents: number
  claimPayoutCents: number
}

export interface Insurance {
  policies: InsurancePolicy[]
  quotes: InsuranceQuote[]
}

export interface RouteInfo {
  id: string
  name: string
  origin: string
  dest: string
  mission: string
  distanceNm: number
  roundTripHours: number
  rewardPerTripCents: number
}

export interface RouteData {
  routes: RouteInfo[]
  bases: { icao: string; name: string }[]
  missions: string[]
}

export interface LoanOffer {
  tier: number
  name: string
  minPrincipalCents: number
  maxPrincipalCents: number
  aprBps: number
}

export interface Loan {
  id: string
  tier: number
  principalCents: number
  outstandingCents: number
  aprBps: number
  termDays: number
  status: string
  takenAt: string
}

export interface Loans {
  loans: Loan[]
  offers: LoanOffer[]
}

export interface NetWorth {
  cashCents: number
  aircraftCents: number
  inventoryCents: number
  loansCents: number
  netWorthCents: number
}

export interface PnlLine {
  category: string
  incomeCents: number
  expenseCents: number
  netCents: number
}

export interface Pnl {
  days: number
  incomeCents: number
  expenseCents: number
  netCents: number
  lines: PnlLine[]
}

export interface FinancesData {
  netWorth: NetWorth
  pnl: Pnl
}

export interface BaseView {
  id: string
  icao: string
  name: string
  isHome: boolean
  rentPerDayCents: number
}

export interface BaseOffer {
  icao: string
  name: string
  kind: string
  distanceNm: number
  openCents: number
  rentPerDayCents: number
}

export interface MarketQuote {
  good: string
  name: string
  buyCents: number
  sellCents: number
  unitWeightLbs: number
}

export interface Inventory {
  id: string
  good: string
  name: string
  quantity: number
  unitCostCents: number
  marketSellCents: number
  unrealizedPnlCents: number
  unitWeightLbs: number
  locationIcao: string
}

export interface TradeResult {
  quantity: number
  proceedsCents: number
  costBasisCents: number
  pnlCents: number
}

export interface VersionInfo {
  version: string
  product: string
}

export interface BackupFile {
  name: string
  sizeBytes: number
  createdUtc: string
}

export interface Achievement {
  key: string
  name: string
  description: string
  category: string
  target: number
  progress: number
  earned: boolean
  earnedAt: string | null
}

export interface CampaignStep {
  title: string
  detail: string
  target: number
  progress: number
  done: boolean
}

export interface Campaign {
  key: string
  name: string
  description: string
  rewardCents: number
  stepIndex: number
  stepCount: number
  completed: boolean
  completedAt: string | null
  steps: CampaignStep[]
}

export interface AirlineIdentity {
  name: string
  tailCode: string
  accentColorHex: string
  emblemKey: string
  customised: boolean
}

export interface StandingContribution { label: string; points: number }

export interface AirlineStanding {
  tier: number
  tierName: string
  score: number
  nextTierScore: number | null
  contributions: StandingContribution[]
}

export interface AirlineData {
  identity: AirlineIdentity
  standing: AirlineStanding
  emblems: string[]
}

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

const sleep = (ms: number) => new Promise(r => setTimeout(r, ms))
const newKey = () =>
  globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`

// A money-committing POST that is safe to retry: one stable Idempotency-Key travels with every attempt,
// so if a response is lost after the server already committed, the retry replays that same outcome
// instead of charging twice. We only retry on a network-level rejection — never on an HTTP error status.
async function POST_IDEM(url: string, body?: unknown): Promise<Response> {
  const key = newKey()
  for (let attempt = 0; ; attempt++) {
    try {
      return await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key },
        body: JSON.stringify(body ?? {}),
      })
    } catch (e) {
      if (attempt >= 2) throw e
      await sleep(150 * (attempt + 1))
    }
  }
}

export const api = {
  /** Current career, or null if none has been started yet. */
  async state(): Promise<State | null> {
    const r = await fetch('/api/game/state')
    if (r.status === 404) return null
    return ok<State>(r)
  },
  newCareer: (name: string, homeIcao: string, startingCash: number) =>
    POST('/api/game/new', { name, homeIcao, startingCash }).then(ok),
  ranks: () => fetch('/api/ranks').then(ok<RankTier[]>),
  reputation: () => fetch('/api/reputation').then(ok<Reputation>),
  loans: () => fetch('/api/loans').then(ok<Loans>),
  finances: () => fetch('/api/finances').then(ok<FinancesData>),
  insurance: () => fetch('/api/insurance').then(ok<Insurance>),
  routes: () => fetch('/api/routes').then(ok<RouteData>),
  createRoute: (body: { name?: string; originIcao: string; destIcao: string; aircraftInstanceId: string; staffId: string; mission: string }) =>
    POST('/api/routes', body).then(ok),
  cancelRoute: (id: string) => POST(`/api/routes/${id}/cancel`).then(ok),
  insure: (aircraftInstanceId: string) => POST_IDEM('/api/insurance/insure', { aircraftInstanceId }).then(ok),
  cancelInsurance: (id: string) => POST(`/api/insurance/${id}/cancel`).then(ok),
  claimInsurance: (id: string) => POST_IDEM(`/api/insurance/${id}/claim`).then(ok<{ paidCents: number }>),
  takeLoan: (principalCents: number) => POST('/api/loans/take', { principalCents }).then(ok),
  payoffLoan: (id: string) => POST_IDEM(`/api/loans/${id}/payoff`).then(ok<{ paidCents: number }>),
  refreshJobs: (count = 8) => POST(`/api/jobs/refresh?count=${count}`).then(ok),
  jobs: () => fetch('/api/jobs').then(ok<Job[]>),
  accept: (id: string) => POST(`/api/jobs/${id}/accept`).then(ok),
  assignments: () => fetch('/api/assignments').then(ok<Assignment[]>),
  beginFlight: (assignmentId: string, aircraftInstanceId?: string) =>
    POST('/api/flight/begin', { assignmentId, aircraftInstanceId }).then(ok),
  ledger: (limit = 50) => fetch(`/api/ledger?limit=${limit}`).then(ok<LedgerEntry[]>),
  flights: () => fetch('/api/flights').then(ok<FlightLog[]>),
  market: () => fetch('/api/aircraft/market').then(ok<AircraftOffer[]>),
  hangar: () => fetch('/api/aircraft').then(ok<OwnedAircraft[]>),
  quals: () => fetch('/api/quals').then(ok<QualClass[]>),
  beginCheckFlight: (cls: string) => POST('/api/checkflights/begin', { class: cls }).then(ok),
  buyAircraft: (typeId: string) => POST_IDEM('/api/aircraft/buy', { typeId }).then(ok),
  maintain: (id: string) => POST_IDEM(`/api/aircraft/${id}/maintain`).then(ok),
  staffCandidates: () => fetch('/api/staff/candidates').then(ok<StaffCandidate[]>),
  staff: () => fetch('/api/staff').then(ok<Staff[]>),
  hire: (candidateSeed: number) => POST('/api/staff/hire', { candidateSeed }).then(ok),
  orders: () => fetch('/api/ops/orders').then(ok<StandingOrder[]>),
  createOrder: (staffId: string, aircraftInstanceId: string, destIcao: string) =>
    POST('/api/ops/orders', { staffId, aircraftInstanceId, destIcao }).then(ok),
  cancelOrder: (id: string) => POST(`/api/ops/orders/${id}/cancel`).then(ok),
  reconcile: () => POST('/api/ops/reconcile').then(ok<ReconcileResult>),
  bases: () => fetch('/api/bases').then(ok<BaseView[]>),
  baseCandidates: () => fetch('/api/bases/candidates').then(ok<BaseOffer[]>),
  openBase: (airportIcao: string) => POST_IDEM('/api/bases/open', { airportIcao }).then(ok),
  tradeMarket: () => fetch('/api/trade/market').then(ok<MarketQuote[]>),
  inventory: () => fetch('/api/trade/inventory').then(ok<Inventory[]>),
  buyGood: (good: string, qty: number) => POST_IDEM('/api/trade/buy', { good, qty }).then(ok),
  sellGood: (good: string, qty: number) => POST_IDEM('/api/trade/sell', { good, qty }).then(ok<TradeResult>),
  achievements: () => fetch('/api/achievements').then(ok<Achievement[]>),
  campaigns: () => fetch('/api/campaigns').then(ok<Campaign[]>),
  airline: () => fetch('/api/airline').then(ok<AirlineData>),
  setAirline: (body: { name: string; tailCode: string; accentColorHex: string; emblemKey: string }) =>
    POST('/api/airline', body).then(ok<AirlineIdentity>),
  version: () => fetch('/api/version').then(ok<VersionInfo>),
  backups: () => fetch('/api/save/backups').then(ok<BackupFile[]>),
  backup: () => POST('/api/save/backup').then(ok<BackupFile>),
  restore: (name: string) => POST('/api/save/restore', { name }).then(ok<{ restart: boolean }>),
  backupDownloadUrl: (name: string) => `/api/save/backups/${encodeURIComponent(name)}/download`,
}

/** Whole-dollar, sign-aware money from integer cents: 147000 -> "$1,470". */
export function money(cents: number): string {
  const dollars = cents / 100
  const sign = dollars < 0 ? '-' : ''
  return `${sign}$${Math.abs(dollars).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}
