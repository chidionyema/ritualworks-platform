using Haworks.Merchant.Domain.Enums;

namespace Haworks.Merchant.Application.Merchants.DTOs;

public sealed record MerchantDto(
    Guid Id,
    Guid OwnerId,
    string Name,
    string Slug,
    string? Bio,
    string? LogoUrl,
    string? Description,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    string? Website,
    MerchantStatus Status,
    IReadOnlyList<OperatingHourDto> OperatingHours);

public sealed record OperatingHourDto(
    DayOfWeek Day,
    TimeSpan Open,
    TimeSpan Close,
    bool IsOpen);
