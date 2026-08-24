namespace Callsign.Host;

public record NewCareerRequest(string? Name, string? HomeIcao, decimal? StartingCash,
    string? StarterTypeCode = null, string? Edition = null, string? AvatarKey = null);

public record StateDto(
    string Name, string Rank, int Xp, int ReputationMilli, string CurrentIcao, string HomeIcao,
    long CashCents, decimal Cash, int Flights, string? AvatarKey = null);

// --- Phase 3f: reputation ---
public record ReputationEventDto(int DeltaMilli, int BalanceMilli, string Reason, DateTimeOffset At);
public record ReputationDto(int ReputationMilli, IReadOnlyList<ReputationEventDto> Events);

public record JobDto(
    Guid Id, string Type, string Origin, string Dest, string DestName, string Commodity,
    int WeightLbs, int Pax, double DistanceNm, long RewardCents, int Xp,
    string RequiredRank, bool Locked, string? LockReason, DateTimeOffset ExpiresAt,
    // Geography + arrival detail — the map, bearing, and "can I land there / what will it cost" readouts.
    double OriginLat, double OriginLon, double DestLat, double DestLon,
    string DestKind, int? DestLongestRunwayFt, long ExpectedLandingFeeCents,
    // The client behind the offer (Phase 8d) + your standing with them and the repeat premium it earns.
    string? ClientName, int ClientLoyaltyMilli, long ExpectedLoyaltyBonusCents,
    long? HubRepBonusCents); // Phase 12 — cents your reputation added because this board is at your hub (11c)

public record AssignmentDto(
    Guid Id, string Type, string Origin, string Dest, string DestName, string Commodity, int WeightLbs, int Pax,
    double DistanceNm, long RewardQuoteCents, int XpQuote, string Status,
    double OriginLat, double OriginLon, double DestLat, double DestLon,
    string DestKind, int? DestLongestRunwayFt, long ExpectedLandingFeeCents, DateTimeOffset? DeadlineAt);

public record PayoutLineDto(string Label, long AmountCents);

public record FlightEventDto(DateTimeOffset At, string Severity, string Message);

public record SettlementDto(Guid FlightId, long PayoutCents, int XpAwarded, bool PayloadMatched, string? PromotedTo, IReadOnlyList<PayoutLineDto> Lines);

// --- Phase 3a: rank ladder (self-documenting reference content) ---
public record RankTierDto(string Rank, string DisplayName, string Description, int MinXp, bool Reached, bool Current);

public record RosterDto(
    string Key, string Name, string Category, bool OnDisk,
    int? Seats, int? UsefulLoadLbs, int? CruiseKtas, int? MinRunwayFt);

public record LedgerDto(DateTimeOffset At, string Category, long AmountCents, string Description);

public record PriceFactorDto(string Label, long AmountCents);

public record AircraftOfferDto(
    Guid TypeId, string Name, string Category, long PriceCents, bool OnDisk,
    int? Seats, int? UsefulLoadLbs, int? CruiseKtas, IReadOnlyList<PriceFactorDto> Factors,
    // Where this airframe physically sits — buying takes delivery HERE (fly it home or ferry it). DistanceNm is
    // measured from where you are now, so it updates as you reposition. LocationName is the field's plain name.
    string LocationIcao, string LocationName, double DistanceNm);

public record OwnedAircraftDto(
    Guid Id, Guid TypeId, string Tail, string Name, string Category, string LocationIcao,
    string Availability, long? PurchasePriceCents, double AirframeHours,
    int HullConditionMilli, int EngineConditionMilli, bool MaintenanceDue, long MaintenanceQuoteCents,
    string RequiredClass, bool Rated,
    string? Manufacturer, string? IcaoTypeDesignator, string? IcaoModel, bool OnDisk,
    int? Seats, int? UsefulLoadLbs, int? FuelCapacityLbs, int? CruiseKtas, int? MinRunwayFt,
    long MarketValueCents, long ResaleValueCents, DateTimeOffset? AcquiredAt,
    double Lat, double Lon, string LocationName,
    double MaintenanceHoursWatermark, double MaintenanceIntervalHours, double HoursToService, int WearMilliPerHour,
    bool Insured, int? CoverageMilli, long? InsuredValueCents,
    int LifetimeFlights, double LifetimeDistanceNm, double LifetimeFuelLbs,
    long LifetimeEarningsCents, long LifetimeUpkeepCents,
    bool Airworthy, string? UnairworthyReason, double HoursTo100h, int DaysToAnnual, long InspectionQuoteCents,
    string Ownership); // Phase 9f — "Owned" | "Rented"; a rented tail can't be sold/insured

// --- Hangar depth (Phase 6): per-airframe drill-down ---
public record AircraftFlightDto(
    Guid Id, string? Origin, string? Dest, string Mission, double DistanceNm, double FuelUsedLbs,
    double TouchdownFpm, long PayoutCents, int Xp, DateTimeOffset SettledAt);

public record AircraftLedgerDto(DateTimeOffset At, string Category, long AmountCents, string Description);

public record AircraftEconomicsDto(
    long PurchasePriceCents, long MarketValueCents, long ResaleValueCents,
    long LifetimeEarningsCents, long LifetimeFuelCents, long LifetimeRepairCents, long LifetimeInsuranceCents,
    long OperatingNetCents, int LifetimeFlights, double LifetimeDistanceNm, double LifetimeFuelLbs,
    double AvgTouchdownFpm, long AvgEarningsPerFlightCents, long OperatingNetPerHourCents);

public record AircraftHistoryDto(
    Guid Id, string Tail, string Name, AircraftEconomicsDto Economics,
    IReadOnlyList<AircraftFlightDto> Flights, IReadOnlyList<AircraftLedgerDto> Ledger);

public record RelocateAircraftRequest(string DestIcao);

// --- Phase 3c: licence classes ---
public record QualClassDto(string Class, string DisplayName, string Description, bool Held, int Stars, long CheckFlightFeeCents);

// --- Phase 3d: check-flights ---
public record CheckFlightBeginRequest(string Class);
public record CheckFlightAttemptRequest(string Class, FlightResultDto Flight);
public record CheckFlightResultDto(bool Passed, string Class, string ClassName, int Stars, long FeeCents, double TouchdownFpm);

public record BuyAircraftRequest(Guid TypeId);
public record UsedListingDto(int Seed, Guid TypeId, string TypeName, string Category, double AirframeHours, int ConditionMilli, long PriceCents, long NewPriceCents,
    // Where the pre-owned airframe sits today — buying takes delivery here, same as a new one.
    string LocationIcao, string LocationName, double DistanceNm);
public record BuyUsedRequest(Guid TypeId, int Seed);

// --- Phase 9f-1: aircraft rental ---
public record RentalOfferDto(Guid TypeId, string TypeName, string Category, long StickerCents, long DailyHoldingCents, long FlightHourCents, long DepositCents, int TermDays);
public record RentAircraftRequest(Guid TypeId, int? TermDays);
public record RentalReturnQuoteDto(long FinalRentCents, long DamageCents, string? DamageReason, long RefundCents);
public record ActiveRentalDto(Guid AgreementId, Guid AircraftInstanceId, string Tail, string TypeName, string LocationIcao, long DepositCents, long AccruedRentCents, int DaysLeft, long FlightHourCents, long DailyHoldingCents, RentalReturnQuoteDto Projected);

// --- Phase 9f-2: aircraft lease + rent-to-own ---
public record LeaseOfferDto(Guid TypeId, string TypeName, string Category, long StickerCents, long WeeklyRateCents, long InsuranceWeeklyCents, long DepositCents, long UpfrontCents, long BuyoutCents, int TermDays);
public record LeaseAircraftRequest(Guid TypeId, int? TermDays);
public record ActiveLeaseDto(Guid AgreementId, Guid AircraftInstanceId, string Tail, string TypeName, string LocationIcao, long DepositCents, long WeeklyRateCents, long InsuranceWeeklyCents, int DaysLeft, long BuyoutCents, bool CasualtyEligible, RentalReturnQuoteDto Projected);

// --- Phase 2d: staff + standing orders ---
public record StaffCandidateDto(int Seed, string Name, long WagePerDayCents, int SkillMilli);
public record StaffDto(Guid Id, string Name, long WagePerDayCents, int SkillMilli, string? CurrentIcao, bool Flying, string Role = "Pilot");
public record HireRequest(int CandidateSeed);
public record HireManagerRequest(string Icao);
public record CheckCentreDto(string Icao, string Name, double DistanceNm, string TestAircraft, long FeeCents, string Class);
public record RelocateCrewRequest(string DestIcao);
public record DispatchJobRequest(Guid StaffId, Guid AircraftInstanceId);
public record DispatchLegDto(Guid Id, Guid StaffId, Guid AircraftInstanceId, string CrewName, string Tail, string AircraftName, string Origin, string Dest, double DistanceNm,
    long RewardCents, string DispatchedAt, string ReadyAt, bool Ready);
public record StandingOrderDto(
    Guid Id, string StaffName, string Tail, string AircraftName, string Origin, string Dest,
    double DistanceNm, double RoundTripHours, long RewardPerTripCents,
    int PriceMultiplierMilli, int FillPct, long FairRewardPerTripCents,
    int PendingTrips = 0, long PendingIncomeCents = 0); // trips accrued since the last reconcile, ready to bank (+ est. income)
public record StandingOrderRequest(Guid StaffId, Guid AircraftInstanceId, string DestIcao, int? PriceMultiplierMilli);
public record OrderPriceRequest(int PriceMultiplierMilli);
public record ReconcileDto(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long LoanCents, long InsuranceCents, long NetCents, int Incidents, IReadOnlyList<string> Grounded, IReadOnlyList<string> DutyMaxed, int EmptyLegs, IReadOnlyList<string> LoanWarnings, IReadOnlyList<string> Defaults, IReadOnlyList<string> CertLapsed, int WeatheredOut, IReadOnlyList<string> CertExpiring, long RentalCents, IReadOnlyList<string> RentalsExpiring, IReadOnlyList<string> RentalsAutoReturned, int OperatingRepDeltaMilli = 0, long FuelCents = 0, long RepairCents = 0);

// --- Phase 4a: loans ---
public record LoanOfferDto(int Tier, string Name, long MinPrincipalCents, long MaxPrincipalCents, int AprBps, int EffectiveAprBps);
public record LoanDto(Guid Id, int Tier, long PrincipalCents, long OutstandingCents, int AprBps, int TermDays, string Status, DateTimeOffset TakenAt);
public record CreditDto(int Score, string Grade, int AprDeltaBps);
public record TakeLoanRequest(long PrincipalCents);

// --- Phase 4d: routes ---
public record RouteDto(Guid Id, string Name, string Origin, string Dest, string Mission, double DistanceNm, double RoundTripHours, long RewardPerTripCents,
    int PriceMultiplierMilli, int FillPct, long FairRewardPerTripCents,
    int? SeatCapacity, int? LoadFactorMilli, // Phase 11f — non-null on a scheduled-passenger route
    string CrewName = "?", string AircraftTail = "?", int PendingTrips = 0, long PendingIncomeCents = 0); // who flies it + trips ready to bank
public record RouteBaseDto(string Icao, string Name);
public record CreateRouteRequest(string? Name, string OriginIcao, string DestIcao, Guid AircraftInstanceId, Guid StaffId, string Mission, int? PriceMultiplierMilli);
public record ScheduledRouteRequest(string? Name, string OriginIcao, string DestIcao, Guid AircraftInstanceId, Guid StaffId); // Phase 11f

// --- Phase 4c: insurance ---
public record InsurancePolicyDto(Guid Id, string Tail, string AircraftName, int ConditionMilli, int CoverageMilli,
    long PremiumPerWeekCents, long DeductibleCents, long ClaimPayoutCents, bool Claimable);
public record InsuranceQuoteDto(Guid AircraftInstanceId, string Tail, string AircraftName,
    long PremiumPerWeekCents, long DeductibleCents, long ClaimPayoutCents);
public record InsureRequest(Guid AircraftInstanceId, int? CoverageMilli);

// --- Phase 4b: balance sheet ---
public record NetWorthDto(long CashCents, long AircraftCents, long InventoryCents, long LoansCents, long NetWorthCents);
public record PnlLineDto(string Category, long IncomeCents, long ExpenseCents, long NetCents);
public record PnlDto(int Days, long IncomeCents, long ExpenseCents, long NetCents, IReadOnlyList<PnlLineDto> Lines);

// --- Finances depth (Phase 6): per-subject P&L, cash-flow series, itemised statement ---
public record AttributionLineDto(string Kind, string Id, string Label, string Sub,
    long IncomeCents, long ExpenseCents, long NetCents, int Entries);
public record CashPointDto(DateTimeOffset At, long IncomeCents, long ExpenseCents, long NetCents, long CashCents);
public record FinanceDetailDto(int Days,
    IReadOnlyList<AttributionLineDto> Aircraft,
    IReadOnlyList<AttributionLineDto> Staff,
    IReadOnlyList<AttributionLineDto> Bases,
    IReadOnlyList<CashPointDto> Series);
public record StatementRowDto(DateTimeOffset At, string Category, long AmountCents, string Description,
    string? Aircraft, string? Staff, string? Base);

// --- Phase 2e: bases ---
public record BaseViewDto(Guid Id, string Icao, string Name, bool IsHome, long RentPerDayCents, double Latitude, double Longitude,
    int MaintenanceLevel, long NextShopUpgradeCents, double MaintenanceDiscountPct,
    int FuelFarmLevel, long NextFuelFarmUpgradeCents, double FuelDiscountPct,
    int HubLevel, long NextHubUpgradeCents, double HubDemandAmplification); // Phase 11e
public record BaseOfferDto(string Icao, string Name, string Kind, double DistanceNm, long OpenCents, long RentPerDayCents, double Latitude, double Longitude);
public record OpenBaseRequest(string AirportIcao);

// --- Phase 2g: trade ---
public record MarketQuoteDto(string Good, string Name, long BuyCents, long SellCents, int UnitWeightLbs, string? Region, int PressurePct, int WeatherPct,
    string? BestSellIcao, long BestSellCents, long BestSellMarginCents, double BestSellDistanceNm, int? ShelfLifeDays = null);
public record WeatherDto(string Icao, string Name, int WindDirDeg, int WindKts, int GustKts, double VisibilitySm, int CeilingFt, int TempC, string Condition, string Summary,
    bool Live = false, string? ObservedIso = null, string? StationIcao = null); // Phase 9b — live-METAR provenance (default = modeled)
public record WorldStateDto(string DateIso, string DayOfWeek, string Season, int CareerDays,
    string EconomyLabel, int EconomyRewardPct);
public record InventoryDto(
    Guid Id, string Good, string Name, int Quantity, long UnitCostCents,
    long MarketSellCents, long UnrealizedPnlCents, int UnitWeightLbs, string LocationIcao,
    int? ShelfLifeDays = null, double? FreshDaysLeft = null, bool Spoiled = false);
public record TradeRequest(string Good, int Qty);
public record TradeResultDto(int Quantity, long ProceedsCents, long CostBasisCents, long PnlCents);

// --- Logbook (Phase 6): the widened flight row, the per-flight drill-down, and lifetime totals ---
public record FlightDto(
    Guid Id, string AircraftTitle, Guid? AircraftTypeId, string? Tail, string? Mission,
    string Origin, string Dest, string DestName,
    double DistanceNm, double FuelUsedLbs, double DurationHours, double TouchdownFpm,
    long PayoutCents, int Xp,
    DateTimeOffset DepartedAt, DateTimeOffset ArrivedAt, DateTimeOffset SettledAt,
    double OriginLat, double OriginLon, double DestLat, double DestLon,
    int? OverallScore, bool? ScoreValid); // Phase 12 — the headline metric (the un-gameable score), not just fpm

public record FlightDetailDto(
    Guid Id, string AircraftTitle, Guid? AircraftTypeId, string? Tail, string? AircraftName, string? AircraftCategory,
    string? Mission, string Origin, string OriginName, string Dest, string DestName,
    double DistanceNm, double FuelUsedLbs, double DurationHours, double TouchdownFpm,
    long PayoutCents, int Xp,
    DateTimeOffset DepartedAt, DateTimeOffset ArrivedAt, DateTimeOffset SettledAt,
    double OriginLat, double OriginLon, double DestLat, double DestLon,
    int? Pax, int? WeightLbs, string? Commodity,
    IReadOnlyList<PayoutLineDto> Lines,
    IReadOnlyList<FlightEventDto> Events,
    int? OverallScore, int? LandingScore, int? ApproachScore, int? ComfortScore,
    double? TouchdownG, bool? StabilizedApproach, int? ViolationPoints, bool? ScoreValid,
    int? OutcomeGrade, string? OutcomeReason,
    DebriefDto Debrief); // Phase 10a — the post-flight coaching debrief

// --- Phase 10a: the post-flight coaching debrief ---
public record DebriefNoteDto(string Tone, string Dimension, string Headline, string Detail);
public record DebriefDto(bool Scored, bool ScoreValid, int OverallScore, string Grade, string Headline,
    IReadOnlyList<DebriefNoteDto> Strengths, IReadOnlyList<DebriefNoteDto> ToImprove);

public record FlightTotalsDto(
    int Flights, double TotalHours, double TotalDistanceNm, double TotalFuelLbs,
    long LifetimeEarningsCents, int TotalXp, double AvgTouchdownFpm, double BestTouchdownFpm,
    long BestPayoutCents, double LongestLegNm,
    int ScoredFlights, double AvgScore, int BestScore); // Phase 12 — lifetime flying quality

public record BeginFlightRequest(Guid AssignmentId, Guid? AircraftInstanceId);

public record FlightLiveDto(
    string Phase, string Connection, Guid? AssignmentId,
    double? AltitudeFt, double? IndicatedAirspeedKts, double? VerticalSpeedFpm, bool? OnGround, string? AircraftTitle);

public record FlightResultDto(
    string AircraftTitle, DateTimeOffset DepartedAt, DateTimeOffset ArrivedAt, double TouchdownFpm, double MaxAltitudeFt,
    double DepartureLat, double DepartureLon, double ArrivalLat, double ArrivalLon, double DistanceNm, double FuelUsedLbs);

// --- Save management: back up / export / restore the save file ---
public record RestoreRequest(string Name);

// --- Phase 5a: achievements ---
public record AchievementDto(
    string Key, string Name, string Description, string Category, long Target, long Progress,
    bool Earned, DateTimeOffset? EarnedAt);

// --- Phase 5b: campaigns ---
public record CampaignStepDto(string Title, string Detail, long Target, long Progress, bool Done);
public record CampaignDto(
    string Key, string Name, string Description, long RewardCents, int StepIndex, int StepCount,
    bool Completed, DateTimeOffset? CompletedAt, IReadOnlyList<CampaignStepDto> Steps);

// --- Phase 12: career highlights (the "your record" readout) ---
public record AircraftUseDto(string Title, int Count);
public record CareerHighlightsDto(
    int TotalFlights, int BlockMinutes, AircraftUseDto? MostUsedAircraft,
    int BestXp, long BestRewardCents, int TotalDistanceNm, int? SmoothestFpm, int? BestScore);

// --- Phase 12: rotating daily/weekly challenges ---
public record ChallengeDto(
    string Key, string Title, string Detail, string Cadence,
    long Target, long Progress, long RewardCents, bool Done, bool Claimed, DateTimeOffset ResetsAt);

// --- Phase 5c: airline identity + standing ---
public record AirlineIdentityDto(string Name, string TailCode, string AccentColorHex, string EmblemKey, bool Customised);
public record StandingContributionDto(string Label, int Points);
// Phase 11b — the career-stage journey (deepened standing).
public record StageRequirementDto(string Label, string Metric, long Current, long Target, string Display, bool Met, bool Primary);
public record StageUnlockDto(string Text, bool Live);
public record CareerStageDto(int Index, string Name, string Tagline, bool Reached, IReadOnlyList<StageRequirementDto> Requirements, IReadOnlyList<StageUnlockDto> Unlocks);
public record NextMoveDto(int StageIndex, string StageName, int MetCount, int TotalCount, int ProgressPct, string Metric, string Label, string Display);
public record AirlineStandingDto(int Stage, string StageName, int Score, IReadOnlyList<StandingContributionDto> Contributions, IReadOnlyList<CareerStageDto> Stages, NextMoveDto? NextMove);
// --- Phase 11a: the airline's operating reputation, with a two-source ("your flying / your crew") split and a log ---
public record AirlineRepEventDto(int DeltaMilli, int BalanceMilli, string Source, string Reason, DateTimeOffset At);
public record AirlineReputationDto(int OperatingReputationMilli, int RecentPlayerDeltaMilli, int RecentCrewDeltaMilli, IReadOnlyList<AirlineRepEventDto> Recent);
public record AirlineDto(AirlineIdentityDto Identity, AirlineStandingDto Standing, AirlineReputationDto Reputation, IReadOnlyList<string> Emblems);
public record SetAirlineRequest(string? Name, string? TailCode, string? AccentColorHex, string? EmblemKey);
