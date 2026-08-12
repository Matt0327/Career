namespace Callsign.Server;

// Wire contracts between the desktop client and Callsign Cloud. Kept deliberately small.

public record RegisterRequest(string Email, string DisplayName, string Password);
public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, ProfileDto User);
public record ProfileDto(string Id, string Email, string DisplayName, string CreatedAt);

/// <summary>What the client shows before deciding to pull: is there a cloud save, and how fresh is it.</summary>
public record SaveMetaDto(bool Exists, long SizeBytes, string? Device, string? UpdatedAt);
