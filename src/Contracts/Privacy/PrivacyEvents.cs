namespace Haworks.Contracts.Privacy;

public record PrivacyErasureRequested
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
}

public record PrivacyErasureCompleted
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
    public required string ServiceName { get; init; }
}

public record PrivacyErasureFailed
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}

public record PrivacyErasureTimedOut
{
    public Guid RequestId { get; init; }
}

public record PrivacyDataExportRequested
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
}

public record PrivacyDataExportCompleted
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public string? DataLink { get; init; }
}

public record PrivacyDataExportFailed
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
    public required string ServiceName { get; init; }
    public required string ErrorMessage { get; init; }
}

public record InitiatePrivacyRequestMessage
{
    public Guid RequestId { get; init; }
    public Guid UserId { get; init; }
}
