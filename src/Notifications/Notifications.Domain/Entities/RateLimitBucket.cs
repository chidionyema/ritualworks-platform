using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Notifications.Domain.Entities;

public sealed class RateLimitBucket : AuditableEntity
{
    public string BucketKey { get; private set; } = string.Empty;
    public DateTime WindowStart { get; private set; }
    public int Count { get; private set; }

    private RateLimitBucket() { }

    public static RateLimitBucket Create() => throw new NotImplementedException("Track L1.C owns this body");
}
