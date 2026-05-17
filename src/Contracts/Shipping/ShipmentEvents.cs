namespace Haworks.Contracts.Shipping;

public sealed record ShipmentCreatedEvent : DomainEvent
{
    public required Guid ShipmentId { get; init; }
    public required Guid OrderId { get; init; }
    public required string CarrierCode { get; init; }
    public required string TrackingNumber { get; init; }
    public required string TrackingUrl { get; init; }
}

public sealed record ShipmentShippedEvent : DomainEvent
{
    public required Guid ShipmentId { get; init; }
    public required Guid OrderId { get; init; }
    public required string CarrierCode { get; init; }
    public required string TrackingNumber { get; init; }
    public DateTime? EstimatedDelivery { get; init; }
}

public sealed record ShipmentDeliveredEvent : DomainEvent
{
    public required Guid ShipmentId { get; init; }
    public required Guid OrderId { get; init; }
    public required DateTime DeliveredAt { get; init; }
}

public sealed record ShipmentExceptionEvent : DomainEvent
{
    public required Guid ShipmentId { get; init; }
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}
