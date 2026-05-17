namespace Haworks.Contracts.Checkout;

/// <summary>
/// Published by RulesEngine when all fraud rules pass.
/// CheckoutOrchestrator proceeds to payment session creation.
/// </summary>
public sealed record FraudCheckPassedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required int RiskScore { get; init; }
}
