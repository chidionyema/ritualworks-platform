namespace Haworks.Contracts.Secrets;

/// <summary>
/// Published when revocation of the old Stripe key fails after all retries.
/// </summary>
public sealed record StripeKeyRevocationFailedEvent
{
    public required Guid RotationId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
}
