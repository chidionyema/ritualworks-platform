namespace Haworks.Contracts.Secrets;

/// <summary>
/// Published by the RotateJwtKeyJob when the JWT signing key is rotated.
/// Consumers re-fetch the new key from Vault (no key material in events).
/// </summary>
public sealed record JwtKeyRotatedEvent : DomainEvent
{
    public required Guid RotationId { get; init; }
    public required DateTimeOffset RotatedAt { get; init; }
}
