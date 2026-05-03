namespace Haworks.Identity.Api.Models;

/// <summary>
/// Request model for user login.
/// </summary>
public sealed record LoginRequest(
    string Username,
    string Password);
