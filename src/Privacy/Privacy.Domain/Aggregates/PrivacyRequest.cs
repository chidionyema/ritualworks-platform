using Haworks.BuildingBlocks.Persistence;
using Haworks.Privacy.Domain.Enums;

namespace Haworks.Privacy.Domain.Aggregates;

public sealed class PrivacyRequest : AuditableEntity
{
    public required Guid UserId { get; init; }
    public required PrivacyRequestType Type { get; init; }
    public PrivacyRequestStatus Status { get; private set; }
    public Guid? ContentId { get; private set; } // For export ZIP in content-svc
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Start() => Status = PrivacyRequestStatus.InProgress;
    
    public void Complete(Guid? contentId = null)
    {
        if (Status == PrivacyRequestStatus.Completed)
            throw new InvalidOperationException("Cannot complete an already-completed privacy request.");

        Status = PrivacyRequestStatus.Completed;
        ContentId = contentId;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail()
    {
        if (Status is PrivacyRequestStatus.Failed or PrivacyRequestStatus.Completed)
            throw new InvalidOperationException("Cannot fail a privacy request that is already in a terminal state.");

        Status = PrivacyRequestStatus.Failed;
    }

    public static PrivacyRequest Create(Guid userId, PrivacyRequestType type)
    {
        return new PrivacyRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            Status = PrivacyRequestStatus.Pending
        };
    }
}
