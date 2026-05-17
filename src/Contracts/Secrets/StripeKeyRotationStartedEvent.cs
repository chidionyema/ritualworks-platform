namespace Haworks.Contracts.Secrets;

/// <summary>
/// Published when a Stripe key rotation begins. The overlap window allows
/// in-flight charges using the old key to complete before revocation.
/// </summary>
public sealed record StripeKeyRotationStartedEvent : DomainEvent
{
    public required Guid RotationId { get; init; }
    public required DateTimeOffset OverlapExpiresAt { get; init; }
}
