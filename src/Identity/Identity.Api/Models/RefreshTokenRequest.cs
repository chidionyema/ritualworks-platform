namespace Haworks.Identity.Api.Models;

/// <summary>
/// Request model for refreshing JWT tokens.
/// </summary>
public sealed record RefreshTokenRequest(
    string AccessToken,
    string RefreshToken);
