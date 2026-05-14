using Haworks.BuildingBlocks.Persistence;
using Haworks.Privacy.Domain.Enums;

namespace Haworks.Privacy.Domain.Aggregates;

public sealed class PrivacyRequestStep : AuditableEntity
{
    public required Guid RequestId { get; init; }
    public required string ServiceName { get; init; }
    public PrivacyRequestStatus Status { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete()
    {
        Status = PrivacyRequestStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string message)
    {
        Status = PrivacyRequestStatus.Failed;
        ErrorMessage = message;
    }

    public static PrivacyRequestStep Create(Guid requestId, string serviceName)
    {
        return new PrivacyRequestStep
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            ServiceName = serviceName,
            Status = PrivacyRequestStatus.Pending
        };
    }
}
