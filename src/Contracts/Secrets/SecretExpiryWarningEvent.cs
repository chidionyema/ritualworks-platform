namespace Haworks.Contracts.Secrets;

/// <summary>
/// Published by the SecretExpiryWatcherJob when a tracked secret approaches
/// its maximum age (e.g., 80% of TTL elapsed without rotation).
/// </summary>
public sealed record SecretExpiryWarningEvent : DomainEvent
{
    public required string SecretPath { get; init; }
    public required double AgePercent { get; init; }
    public required DateTimeOffset LastRotatedAt { get; init; }
}
