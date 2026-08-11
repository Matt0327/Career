namespace Callsign.Host;

public record NewCareerRequest(string? Name, string? HomeIcao, decimal? StartingCash);

public record StateDto(
    string Name, string Rank, int Xp, string CurrentIcao, string HomeIcao,
    long CashCents, decimal Cash, int Flights);

public record JobDto(
    Guid Id, string Type, string Origin, string Dest, string DestName, string Commodity,
    int WeightLbs, int Pax, double DistanceNm, long RewardCents, int Xp,
    string RequiredRank, bool Locked, string? LockReason, DateTimeOffset ExpiresAt);

public record AssignmentDto(
    Guid Id, string Type, string Origin, string Dest, string DestName, string Commodity, int WeightLbs, int Pax,
    double DistanceNm, long RewardQuoteCents, int XpQuote, string Status);

public record PayoutLineDto(string Label, long AmountCents);

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
    int? Seats, int? UsefulLoadLbs, int? CruiseKtas, IReadOnlyList<PriceFactorDto> Factors);

public record OwnedAircraftDto(
    Guid Id, string Tail, string Name, string Category, string LocationIcao,
    string Availability, long? PurchasePriceCents, double AirframeHours,
    int HullConditionMilli, int EngineConditionMilli, bool MaintenanceDue, long MaintenanceQuoteCents,
    string RequiredClass, bool Rated);

// --- Phase 3c: licence classes ---
public record QualClassDto(string Class, string DisplayName, string Description, bool Held, int Stars);

public record BuyAircraftRequest(Guid TypeId);

// --- Phase 2d: staff + standing orders ---
public record StaffCandidateDto(int Seed, string Name, long WagePerDayCents, int SkillMilli);
public record StaffDto(Guid Id, string Name, long WagePerDayCents, int SkillMilli);
public record HireRequest(int CandidateSeed);
public record StandingOrderDto(
    Guid Id, string StaffName, string Tail, string Origin, string Dest,
    double DistanceNm, double RoundTripHours, long RewardPerTripCents);
public record StandingOrderRequest(Guid StaffId, Guid AircraftInstanceId, string DestIcao);
public record ReconcileDto(int Trips, long GrossIncomeCents, long FeesCents, long WagesCents, long RentCents, long NetCents);

// --- Phase 2e: bases ---
public record BaseViewDto(Guid Id, string Icao, string Name, bool IsHome, long RentPerDayCents);
public record BaseOfferDto(string Icao, string Name, string Kind, double DistanceNm, long OpenCents, long RentPerDayCents);
public record OpenBaseRequest(string AirportIcao);

// --- Phase 2g: trade ---
public record MarketQuoteDto(string Good, string Name, long BuyCents, long SellCents, int UnitWeightLbs);
public record InventoryDto(
    Guid Id, string Good, string Name, int Quantity, long UnitCostCents,
    long MarketSellCents, long UnrealizedPnlCents, int UnitWeightLbs, string LocationIcao);
public record TradeRequest(string Good, int Qty);
public record TradeResultDto(int Quantity, long ProceedsCents, long CostBasisCents, long PnlCents);

public record FlightDto(Guid Id, string AircraftTitle, double TouchdownFpm, long PayoutCents, int Xp, DateTimeOffset SettledAt);

public record BeginFlightRequest(Guid AssignmentId, Guid? AircraftInstanceId);

public record FlightLiveDto(
    string Phase, string Connection, Guid? AssignmentId,
    double? AltitudeFt, double? IndicatedAirspeedKts, double? VerticalSpeedFpm, bool? OnGround, string? AircraftTitle);

public record FlightResultDto(
    string AircraftTitle, DateTimeOffset DepartedAt, DateTimeOffset ArrivedAt, double TouchdownFpm, double MaxAltitudeFt,
    double DepartureLat, double DepartureLon, double ArrivalLat, double ArrivalLon, double DistanceNm, double FuelUsedLbs);
