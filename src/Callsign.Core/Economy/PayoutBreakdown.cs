namespace Callsign.Core.Economy;

/// <summary>One itemised line of a payout — maps to exactly one ledger row.</summary>
public sealed record PayoutLine(string Label, long AmountCents);

/// <summary>The full itemised payout for a flight. <c>Lines</c> sum to <c>TotalCents</c>.</summary>
public sealed record PayoutBreakdown(long TotalCents, IReadOnlyList<PayoutLine> Lines);

/// <summary>What a settlement produced.</summary>
public sealed record SettlementResult(
    Guid FlightId, long PayoutCents, int XpAwarded, bool PayloadMatched, PayoutBreakdown Breakdown);
