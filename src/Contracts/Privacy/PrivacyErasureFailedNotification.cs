namespace Haworks.Contracts.Privacy;

/// <summary>
/// Published when a GDPR erasure request fails so downstream systems
/// (Notifications, Audit, etc.) can alert and log compliance events.
/// </summary>
public sealed record PrivacyErasureFailedNotification : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}
