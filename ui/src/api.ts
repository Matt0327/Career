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
  originLat: number
  originLon: number
  destLat: number
  destLon: number
  destKind: string
  destLongestRunwayFt: number | null
  expectedLandingFeeCents: number
  clientName?: string | null
  clientLoyaltyMilli: number // your standing with this client, 0..100000 (Phase 8d)
  expectedLoyaltyBonusCents: number // the repeat premium they'd pay on this job at current loyalty
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
  originLat: number
  originLon: number
  destLat: number
  destLon: number
  destKind: string
  destLongestRunwayFt: number | null
  expectedLandingFeeCents: number
  deadlineAt: string | null // Phase 7d — delivery deadline for time-critical missions (Express/Emergency)
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
  aircraftTypeId: string | null
  tail: string | null
  mission: string | null
  origin: string
  dest: string
  destName: string
  distanceNm: number
  fuelUsedLbs: number
  durationHours: number
  touchdownFpm: number
  payoutCents: number
  xp: number
  departedAt: string
  arrivedAt: string
  settledAt: string
  originLat: number
  originLon: number
  destLat: number
  destLon: number
}

export interface PayoutLine { label: string; amountCents: number }

/** A real scored moment from a flight (Phase 7a) — takeoff, the touchdown + its quality, a taxi overspeed. */
export interface FlightEvent { at: string; severity: string; message: string }

export interface FlightDetail {
  id: string
  aircraftTitle: string
  aircraftTypeId: string | null
  tail: string | null
  aircraftName: string | null
  aircraftCategory: string | null
  mission: string | null
  origin: string
  originName: string
  dest: string
  destName: string
  distanceNm: number
  fuelUsedLbs: number
  durationHours: number
  touchdownFpm: number
  payoutCents: number
  xp: number
  departedAt: string
  arrivedAt: string
  settledAt: string
  originLat: number
  originLon: number
  destLat: number
  destLon: number
  pax: number | null
  weightLbs: number | null
  commodity: string | null
  lines: PayoutLine[]
  events: FlightEvent[]
  // Phase 7b scored assessment (null on flights recorded before 7b).
  overallScore: number | null
  landingScore: number | null
  approachScore: number | null
  touchdownG: number | null
  stabilizedApproach: boolean | null
  violationPoints: number | null
  scoreValid: boolean | null
  // Phase 7d — mission completion grade (2 = Partial, 3 = Failed; null = an ordinary job).
  outcomeGrade: number | null
  outcomeReason: string | null
}

export interface FlightTotals {
  flights: number
  totalHours: number
  totalDistanceNm: number
  totalFuelLbs: number
  lifetimeEarningsCents: number
  totalXp: number
  avgTouchdownFpm: number
  bestTouchdownFpm: number
  bestPayoutCents: number
  longestLegNm: number
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

export interface UsedListing {
  seed: number
  typeId: string
  typeName: string
  category: string
  airframeHours: number
  conditionMilli: number
  priceCents: number
  newPriceCents: number
}

export interface OwnedAircraft {
  id: string
  typeId: string
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
  manufacturer: string | null
  icaoTypeDesignator: string | null
  icaoModel: string | null
  onDisk: boolean
  seats: number | null
  usefulLoadLbs: number | null
  fuelCapacityLbs: number | null
  cruiseKtas: number | null
  minRunwayFt: number | null
  marketValueCents: number
  resaleValueCents: number
  acquiredAt: string | null
  lat: number
  lon: number
  locationName: string
  maintenanceHoursWatermark: number
  maintenanceIntervalHours: number
  hoursToService: number
  wearMilliPerHour: number
  insured: boolean
  coverageMilli: number | null
  insuredValueCents: number | null
  lifetimeFlights: number
  lifetimeDistanceNm: number
  lifetimeFuelLbs: number
  lifetimeEarningsCents: number
  lifetimeUpkeepCents: number
  // Phase 7e — airworthiness & inspections
  airworthy: boolean
  unairworthyReason: string | null
  hoursTo100h: number
  daysToAnnual: number
  inspectionQuoteCents: number
}

export interface AircraftFlight {
  id: string
  origin: string | null
  dest: string | null
  mission: string
  distanceNm: number
  fuelUsedLbs: number
  touchdownFpm: number
  payoutCents: number
  xp: number
  settledAt: string
}

export interface AircraftLedger { at: string; category: string; amountCents: number; description: string }

export interface AircraftEconomics {
  purchasePriceCents: number
  marketValueCents: number
  resaleValueCents: number
  lifetimeEarningsCents: number
  lifetimeFuelCents: number
  lifetimeRepairCents: number
  lifetimeInsuranceCents: number
  operatingNetCents: number
  lifetimeFlights: number
  lifetimeDistanceNm: number
  lifetimeFuelLbs: number
  avgTouchdownFpm: number
  avgEarningsPerFlightCents: number
  operatingNetPerHourCents: number
}

export interface AircraftHistory {
  id: string
  tail: string
  name: string
  economics: AircraftEconomics
  flights: AircraftFlight[]
  ledger: AircraftLedger[]
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

/** A real scored moment streamed live as the flight happens (Phase 7a) — same event that gets
 *  persisted at settlement, so the live log and the logbook tell the identical, true story. */
export interface LiveEvent {
  type: 'event'
  severity: string // FlightEventSeverity: Info | Success | Warning
  message: string
  at: string
  phase: string
}

export type WsEvent = Telemetry | Settled | LinkState | Diverted | CheckFlightDone | LiveEvent

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
  rewardPerTripCents: number // effective per FILLED trip at your markup
  priceMultiplierMilli: number // Phase 7g — your markup over the fair rate (1000 = fair)
  fillPct: number // Phase 7g — expected share of trips the client actually ships at this price
  fairRewardPerTripCents: number // Phase 7g — the frozen fair rate the markup rides on
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
  incidents: number // Phase 7f — autonomous trips a crew botched (fewer with a higher-skill pilot)
  grounded: string[] // Phase 7e — tails that couldn't fly their autonomous work (grounded, needs service)
  dutyMaxed: string[] // Phase 7f — tails whose lone crew hit the daily duty limit (hire more crew)
  emptyLegs: number // Phase 7g — legs that flew empty because a marked-up line priced out the client
  loanWarnings: string[] // Phase 7g — loans in forbearance, warned before they default (Law 4)
  defaults: string[] // Phase 7g — loans that defaulted this pass (charged off, credit wrecked)
  certLapsed: string[] // Phase 8e — routes held because their operating certificate lapsed (renew to resume)
  weatheredOut: number // Phase 8f — autonomous trips scrubbed by foul weather at the origin this pass
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
  rewardPerTripCents: number // effective per FILLED trip at your markup
  priceMultiplierMilli: number // Phase 7g — your markup over the fair rate (1000 = fair)
  fillPct: number // Phase 7g — expected share of trips the client actually ships at this price
  fairRewardPerTripCents: number // Phase 7g — the frozen fair rate the markup rides on
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
  aprBps: number // the tier's listed rate
  effectiveAprBps: number // Phase 7g — the rate priced for your credit rating
}

export interface Credit {
  score: number // Phase 7g — 0-100 borrowing standing from repayment history + leverage
  grade: string // A–E
  aprDeltaBps: number // signed adjustment to every tier's APR (negative = discount)
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
  credit: Credit
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

export interface AttributionLine {
  kind: string
  id: string
  label: string
  sub: string
  incomeCents: number
  expenseCents: number
  netCents: number
  entries: number
}

export interface CashPoint {
  at: string
  incomeCents: number
  expenseCents: number
  netCents: number
  cashCents: number
}

export interface FinanceDetail {
  days: number
  aircraft: AttributionLine[]
  staff: AttributionLine[]
  bases: AttributionLine[]
  series: CashPoint[]
}

export interface StatementRow {
  at: string
  category: string
  amountCents: number
  description: string
  aircraft: string | null
  staff: string | null
  base: string | null
}

export interface BaseView {
  id: string
  icao: string
  name: string
  isHome: boolean
  rentPerDayCents: number
  latitude: number
  longitude: number
  // Phase 7g — maintenance shop
  maintenanceLevel: number
  nextShopUpgradeCents: number
  maintenanceDiscountPct: number
  // Phase 7g — fuel farm
  fuelFarmLevel: number
  nextFuelFarmUpgradeCents: number
  fuelDiscountPct: number
}

export interface BaseOffer {
  icao: string
  name: string
  kind: string
  distanceNm: number
  openCents: number
  rentPerDayCents: number
  latitude: number
  longitude: number
}

export interface MarketQuote {
  good: string
  name: string
  buyCents: number
  sellCents: number
  unitWeightLbs: number
  region: string | null // Phase 7g — "export" (cheap here, buy) | "demand" (dear here, sell) | null
  pressurePct: number // Phase 7g — how far YOUR trading moved this price (+ bid up, − softened), decays to 0
  weatherPct: number // Phase 8f — how far the local weather lifted this price (foul air pays dearer), 0 in clear
}

export interface WorldState {
  dateIso: string
  dayOfWeek: string
  season: string // Spring | Summer | Autumn | Winter (hemisphere-correct)
  careerDays: number // whole days the company has operated
  economyLabel: string // Boom | Bust | Recovery | Slowing (the macro cycle, Phase 8c)
  economyRewardPct: number // signed % the cycle adds to fresh job pay (e.g. +12, -8)
}

// A named client relationship (Phase 8d) at its current, decayed standing.
export interface Client {
  name: string
  homeIcao: string
  loyaltyMilli: number // 0..100000, decayed to now (a neglected client cools)
  jobsCompleted: number
  jobsFailed: number
  lastJobAt?: string | null
  firstSeenAt: string
}

// An operating certificate's standing (Phase 8e) — held/valid + the standards bar to earn it.
export interface CertificateStatus {
  kind: string
  displayName: string
  blurb: string
  gatesLabels: string[]
  held: boolean
  valid: boolean
  issuedAt?: string | null
  expiresAt?: string | null
  daysLeft?: number | null
  feeCents: number
  validityDays: number
  minReputationMilli: number
  minCompletedFlights: number
  reputationMilli: number
  completedFlights: number
  cashCents: number
  meetsReputation: boolean
  meetsRecord: boolean
  canAfford: boolean
  canApply: boolean
  blocker?: string | null
}

export interface Weather {
  icao: string
  name: string
  windDirDeg: number
  windKts: number
  gustKts: number
  visibilitySm: number
  ceilingFt: number
  tempC: number
  condition: string // Clear | Cloudy | Rain | Snow | Fog | Storm
  summary: string
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

export interface CloudStatus {
  signedIn: boolean
  email?: string | null
  displayName?: string | null
  baseUrl: string
}

export interface CloudSaveMeta {
  exists: boolean
  sizeBytes: number
  device?: string | null
  updatedAt?: string | null
}

export interface CloudResult {
  ok: boolean
  error?: string
}

export interface ImageMeta {
  attribution?: string | null
  license?: string | null
  sourceUrl?: string | null
}

export interface LeaderboardRow {
  position: number
  displayName: string
  value: number
  rankKey?: string | null
  isYou: boolean
}

export interface MyStanding {
  netWorth?: number | null
  flights?: number | null
  reputation?: number | null
  xp?: number | null
}

async function ok<T>(r: Response): Promise<T> {
  if (!r.ok) throw new Error(`${r.status} ${r.statusText}: ${await r.text()}`)
  return r.json() as Promise<T>
}

// Cloud calls return a plain ok/error result so the Account panel can show the server's message directly
// (the Host replies with { error } on failure) rather than a raw HTTP string.
async function cloudPost(url: string, body?: unknown): Promise<CloudResult> {
  const r = await POST(url, body)
  if (r.ok) return { ok: true }
  let error = `Request failed (${r.status}).`
  try { const j = await r.json(); if (j?.error) error = j.error } catch { /* keep the generic message */ }
  return { ok: false, error }
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
  finances: (days?: number) =>
    fetch(days ? `/api/finances?days=${days}` : '/api/finances').then(ok<FinancesData>),
  financesDetail: (days: number) => fetch(`/api/finances/detail?days=${days}`).then(ok<FinanceDetail>),
  statement: (days: number) => fetch(`/api/finances/statement?days=${days}`).then(ok<StatementRow[]>),
  insurance: () => fetch('/api/insurance').then(ok<Insurance>),
  routes: () => fetch('/api/routes').then(ok<RouteData>),
  createRoute: (body: { name?: string; originIcao: string; destIcao: string; aircraftInstanceId: string; staffId: string; mission: string; priceMultiplierMilli?: number }) =>
    POST('/api/routes', body).then(ok),
  setRoutePrice: (id: string, priceMultiplierMilli: number) =>
    POST(`/api/routes/${id}/price`, { priceMultiplierMilli }).then(ok),
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
  flights: (skip = 0, take = 50): Promise<FlightLog[]> => fetch(`/api/flights?skip=${skip}&take=${take}`).then(ok<FlightLog[]>),
  flight: (id: string) => fetch(`/api/flights/${id}`).then(ok<FlightDetail>),
  flightTotals: () => fetch('/api/flights/totals').then(ok<FlightTotals>),
  market: () => fetch('/api/aircraft/market').then(ok<AircraftOffer[]>),
  hangar: () => fetch('/api/aircraft').then(ok<OwnedAircraft[]>),
  aircraftThumbUrl: (typeId: string) => `/api/aircraft/type/${typeId}/thumbnail`,
  aircraftImageUrl: (typeId: string) => `/api/aircraft/type/${typeId}/image`,
  aircraftImageMeta: (typeId: string) => fetch(`/api/aircraft/type/${typeId}/image/meta`).then(ok<ImageMeta>),
  quals: () => fetch('/api/quals').then(ok<QualClass[]>),
  beginCheckFlight: (cls: string) => POST('/api/checkflights/begin', { class: cls }).then(ok),
  buyAircraft: (typeId: string) => POST_IDEM('/api/aircraft/buy', { typeId }).then(ok),
  usedMarket: () => fetch('/api/aircraft/used').then(ok<UsedListing[]>),
  buyUsed: (typeId: string, seed: number) => POST_IDEM('/api/aircraft/buy-used', { typeId, seed }).then(ok),
  maintain: (id: string) => POST_IDEM(`/api/aircraft/${id}/maintain`).then(ok),
  inspect: (id: string) => POST_IDEM(`/api/aircraft/${id}/inspect`).then(ok),
  aircraftHistory: (id: string) => fetch(`/api/aircraft/${id}/history`).then(ok<AircraftHistory>),
  sellAircraft: (id: string) => POST_IDEM(`/api/aircraft/${id}/sell`).then(ok<{ proceedsCents: number }>),
  relocateAircraft: (id: string, destIcao: string) =>
    POST_IDEM(`/api/aircraft/${id}/relocate`, { destIcao }).then(ok<{ feeCents: number }>),
  staffCandidates: () => fetch('/api/staff/candidates').then(ok<StaffCandidate[]>),
  staff: () => fetch('/api/staff').then(ok<Staff[]>),
  hire: (candidateSeed: number) => POST('/api/staff/hire', { candidateSeed }).then(ok),
  orders: () => fetch('/api/ops/orders').then(ok<StandingOrder[]>),
  createOrder: (staffId: string, aircraftInstanceId: string, destIcao: string, priceMultiplierMilli = 1000) =>
    POST('/api/ops/orders', { staffId, aircraftInstanceId, destIcao, priceMultiplierMilli }).then(ok),
  setOrderPrice: (id: string, priceMultiplierMilli: number) =>
    POST(`/api/ops/orders/${id}/price`, { priceMultiplierMilli }).then(ok),
  cancelOrder: (id: string) => POST(`/api/ops/orders/${id}/cancel`).then(ok),
  reconcile: () => POST('/api/ops/reconcile').then(ok<ReconcileResult>),
  bases: () => fetch('/api/bases').then(ok<BaseView[]>),
  baseCandidates: () => fetch('/api/bases/candidates').then(ok<BaseOffer[]>),
  openBase: (airportIcao: string) => POST_IDEM('/api/bases/open', { airportIcao }).then(ok),
  upgradeShop: (id: string) => POST_IDEM(`/api/bases/${id}/upgrade-shop`).then(ok<{ maintenanceLevel: number }>),
  upgradeFuelFarm: (id: string) => POST_IDEM(`/api/bases/${id}/upgrade-fuel-farm`).then(ok<{ fuelFarmLevel: number }>),
  world: () => fetch('/api/world').then(ok<WorldState>),
  weather: (icao?: string) => fetch('/api/weather' + (icao ? `?icao=${encodeURIComponent(icao)}` : '')).then(ok<Weather>),
  clients: () => fetch('/api/clients').then(ok<Client[]>),
  certificates: () => fetch('/api/certificates').then(ok<CertificateStatus[]>),
  applyCertificate: (kind: string) => POST_IDEM(`/api/certificates/${encodeURIComponent(kind)}/apply`).then(ok<{ kind: string; issuedAt: string; expiresAt: string }>),
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

  // Callsign Cloud — accounts + cloud saves, proxied through the local Host.
  cloud: {
    status: () => fetch('/api/cloud/status').then(ok<CloudStatus>),
    saveMeta: () => fetch('/api/cloud/save/meta').then(ok<CloudSaveMeta>),
    register: (email: string, displayName: string, password: string) =>
      cloudPost('/api/cloud/register', { email, displayName, password }),
    login: (email: string, password: string) => cloudPost('/api/cloud/login', { email, password }),
    logout: () => cloudPost('/api/cloud/logout'),
    push: () => cloudPost('/api/cloud/push'),
    pull: () => cloudPost('/api/cloud/pull'),
    leaderboard: (board: string) => fetch(`/api/cloud/leaderboard?board=${encodeURIComponent(board)}`).then(ok<LeaderboardRow[]>),
    submitStanding: () => cloudPost('/api/cloud/leaderboard/submit'),
    reportAircraft: () => cloudPost('/api/cloud/aircraft/report'),
  },
}

/** Whole-dollar, sign-aware money from integer cents: 147000 -> "$1,470". */
export function money(cents: number): string {
  const dollars = cents / 100
  const sign = dollars < 0 ? '-' : ''
  return `${sign}$${Math.abs(dollars).toLocaleString('en-US', { maximumFractionDigits: 0 })}`
}
