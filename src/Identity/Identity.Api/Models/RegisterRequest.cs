namespace Haworks.Identity.Api.Models;

/// <summary>
/// Request model for user registration.
/// </summary>
public sealed record RegisterRequest(
    string Username,
    string Email,
    string Password);
