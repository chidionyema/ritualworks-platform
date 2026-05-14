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
        Status = PrivacyRequestStatus.Completed;
        ContentId = contentId;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Fail() => Status = PrivacyRequestStatus.Failed;

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
