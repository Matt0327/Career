namespace Callsign.Server;

// Wire contracts between the desktop client and Callsign Cloud. Kept deliberately small.

public record RegisterRequest(string Email, string DisplayName, string Password);
public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, ProfileDto User);
public record ProfileDto(string Id, string Email, string DisplayName, string CreatedAt);

/// <summary>What the client shows before deciding to pull: is there a cloud save, and how fresh is it.</summary>
public record SaveMetaDto(bool Exists, long SizeBytes, string? Device, string? UpdatedAt);

/// <summary>An admin's decision on a pending aircraft image.</summary>
public record ModerateRequest(bool Approve);

/// <summary>The app's snapshot of a player's standing, pushed to the leaderboard.</summary>
public record LeaderboardSubmit(long NetWorthCents, int Flights, int ReputationMilli, long Xp, string? RankKey);

/// <summary>One ranked line on a board.</summary>
public record LeaderboardRow(int Position, string DisplayName, long Value, string? RankKey, bool IsYou);

/// <summary>A player's own position (1-based) on each board, or null if they haven't submitted.</summary>
public record MyStanding(int? NetWorth, int? Flights, int? Reputation, int? Xp);
