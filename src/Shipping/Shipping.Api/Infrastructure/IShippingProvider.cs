namespace Haworks.Shipping.Api.Infrastructure;

/// <summary>
/// Abstraction over shipping provider (EasyPost, ShipEngine, etc.)
/// Allows swap without touching business logic.
/// </summary>
public interface IShippingProvider
{
    Task<CreateShipmentResult> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken ct);
    Task<BuyLabelResult> BuyLabelAsync(string shipmentId, string rateId, CancellationToken ct);
    Task<TrackingInfo> GetTrackingAsync(string trackingNumber, CancellationToken ct);
}

public sealed record CreateShipmentRequest(
    AddressDto From,
    AddressDto To,
    ParcelDto Parcel);

public sealed record AddressDto(
    string Street,
    string City,
    string State,
    string Zip,
    string Country);

public sealed record ParcelDto(
    double LengthInches,
    double WidthInches,
    double HeightInches,
    double WeightOz);

public sealed record CreateShipmentResult(
    string ShipmentId,
    IReadOnlyList<RateDto> Rates);

public sealed record RateDto(
    string RateId,
    string Carrier,
    string Service,
    decimal Amount,
    string Currency,
    int? EstDeliveryDays);

public sealed record BuyLabelResult(
    string TrackingNumber,
    string TrackingUrl,
    string LabelUrl,
    string Carrier,
    string Service,
    decimal Amount,
    string Currency,
    DateTime? EstimatedDelivery);

public sealed record TrackingInfo(
    string Status,
    string? Description,
    DateTime? EstimatedDelivery,
    IReadOnlyList<TrackingEvent> Events);

public sealed record TrackingEvent(
    DateTime Timestamp,
    string Status,
    string? Location,
    string? Description);
