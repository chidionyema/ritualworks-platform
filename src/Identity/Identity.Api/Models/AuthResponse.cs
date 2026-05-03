namespace Haworks.Identity.Api.Models;

/// <summary>
/// Response model for authentication operations.
/// </summary>
public sealed record AuthResponse(
    string Token,
    string? RefreshToken,
    string UserId,
    string? Username,
    string? Email,
    DateTime Expires,
    string? Message = null);
