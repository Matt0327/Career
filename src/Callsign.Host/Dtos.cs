namespace Callsign.Host;

public record NewCareerRequest(string? Name, string? HomeIcao, decimal? StartingCash);

public record StateDto(
    string Name, string Rank, int Xp, string CurrentIcao, string HomeIcao,
    long CashCents, decimal Cash, int Flights);

public record JobDto(
    Guid Id, string Type, string Origin, string Dest, string DestName, string Commodity,
    int WeightLbs, double DistanceNm, long RewardCents, int Xp, DateTimeOffset ExpiresAt);

public record AssignmentDto(
    Guid Id, string Origin, string Dest, string DestName, string Commodity, int WeightLbs,
    double DistanceNm, long RewardQuoteCents, int XpQuote, string Status);

public record PayoutLineDto(string Label, long AmountCents);

public record SettlementDto(Guid FlightId, long PayoutCents, int XpAwarded, bool PayloadMatched, IReadOnlyList<PayoutLineDto> Lines);

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
    string Availability, long? PurchasePriceCents, double AirframeHours);

public record BuyAircraftRequest(Guid TypeId);

public record FlightDto(Guid Id, string AircraftTitle, double TouchdownFpm, long PayoutCents, int Xp, DateTimeOffset SettledAt);

public record BeginFlightRequest(Guid AssignmentId);

public record FlightLiveDto(
    string Phase, string Connection, Guid? AssignmentId,
    double? AltitudeFt, double? IndicatedAirspeedKts, double? VerticalSpeedFpm, bool? OnGround, string? AircraftTitle);

public record FlightResultDto(
    string AircraftTitle, DateTimeOffset DepartedAt, DateTimeOffset ArrivedAt, double TouchdownFpm, double MaxAltitudeFt,
    double DepartureLat, double DepartureLon, double ArrivalLat, double ArrivalLon, double DistanceNm, double FuelUsedLbs);
