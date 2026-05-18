namespace Haworks.Contracts.Checkout;

/// <summary>
/// Published by CheckoutOrchestrator after stock is reserved, before payment.
/// RulesEngine evaluates fraud rules and responds with Passed or Failed.
/// </summary>
public sealed record FraudCheckRequestedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required string UserId { get; init; }
    public required long TotalAmountCents { get; init; }
    public required string Currency { get; init; }
    public required string CustomerEmail { get; init; }
    public required int ItemCount { get; init; }
    public required bool IsGuest { get; init; }
    public string? CustomerIpAddress { get; init; }
    public string? CustomerCountryCode { get; init; }
}
