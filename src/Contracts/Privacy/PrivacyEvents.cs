namespace Haworks.Contracts.Privacy;

public sealed record PrivacyErasureRequested : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
}

public sealed record PrivacyErasureCompleted : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
}

public sealed record PrivacyErasureFailed : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record PrivacyErasureTimedOut : DomainEvent
{
    public required Guid RequestId { get; init; }
}

public sealed record PrivacyDataExportRequested : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
}

public sealed record PrivacyDataExportCompleted : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public string? DataLink { get; init; }
}

public sealed record PrivacyDataExportFailed : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed record InitiatePrivacyRequestMessage : DomainEvent
{
    public required Guid RequestId { get; init; }
    public required Guid UserId { get; init; }
}
