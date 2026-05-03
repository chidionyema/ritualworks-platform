namespace Haworks.Identity.Api.Models;

/// <summary>
/// Response model for token verification.
/// </summary>
public sealed record TokenVerificationResponse(
    string UserId,
    string? UserName,
    bool IsAuthenticated,
    string? Message = null);
