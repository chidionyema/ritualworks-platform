namespace Haworks.Identity.Api.Models;

public sealed record ProfileResponse(
    string FirstName,
    string LastName,
    string? Email,
    string? Phone,
    string? Address,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? Bio,
    string? Website,
    string? AvatarUrl);

public sealed record SaveShippingInfoRequest(
    string FirstName,
    string LastName,
    string Address,
    string City,
    string PostalCode,
    string Country = "US",
    string? State = null,
    string? Phone = null);

public sealed record UpdateProfileRequest(
    string FirstName,
    string LastName,
    string? Phone = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null,
    string Country = "US",
    string? Bio = null,
    string? Website = null);
