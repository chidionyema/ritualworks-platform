namespace Haworks.Contracts.Checkout;

/// <summary>
/// Published by RulesEngine when fraud rules fail.
/// CheckoutOrchestrator transitions to Abandoned (releases stock).
/// </summary>
public sealed record FraudCheckFailedEvent : DomainEvent
{
    public required Guid SagaId { get; init; }
    public required Guid OrderId { get; init; }
    public required int RiskScore { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<string> TriggeredRules { get; init; }
}
