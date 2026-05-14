using Haworks.BuildingBlocks.Persistence;
using Haworks.Merchant.Domain.Enums;

namespace Haworks.Merchant.Domain.Aggregates;

public sealed class MerchantProfile : AuditableEntity
{
    public required Guid OwnerId { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Bio { get; set; }
    public MerchantStatus Status { get; private set; }

    public void Activate() => Status = MerchantStatus.Active;
    public void Suspend() => Status = MerchantStatus.Suspended;

    public static MerchantProfile Create(Guid ownerId, string name, string slug)
    {
        return new MerchantProfile
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            Slug = slug,
            Status = MerchantStatus.Active
        };
    }
}
