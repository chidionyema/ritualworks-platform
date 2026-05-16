using Haworks.BuildingBlocks.Persistence;
using Haworks.Merchant.Domain.Enums;

namespace Haworks.Merchant.Domain.Aggregates;

public sealed class MerchantProfile : AuditableEntity
{
    public required Guid OwnerId { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Bio { get; set; }
    public string? LogoUrl { get; set; }
    public string? Description { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Category { get; set; }
    public string? Website { get; set; }
    public string? Timezone { get; set; }
    public string? RejectionReason { get; private set; }
    public string? SuspensionReason { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? RejectedAt { get; private set; }
    public DateTimeOffset? SuspendedAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public string? ApprovedBy { get; private set; }
    public string? RejectedBy { get; private set; }
    public string? SuspendedBy { get; private set; }
    public string? DeactivatedBy { get; private set; }
    public MerchantStatus Status { get; private set; }

    public void Activate(string approvedBy)
    {
        if (Status is not (MerchantStatus.Pending or MerchantStatus.Suspended))
            throw new InvalidOperationException($"Cannot transition from {Status} to Active");

        Status = MerchantStatus.Active;
        ApprovedAt = DateTimeOffset.UtcNow;
        ApprovedBy = approvedBy;
    }

    public void Suspend(string suspendedBy, string reason)
    {
        if (Status != MerchantStatus.Active)
            throw new InvalidOperationException($"Cannot transition from {Status} to Suspended");

        Status = MerchantStatus.Suspended;
        SuspendedAt = DateTimeOffset.UtcNow;
        SuspendedBy = suspendedBy;
        SuspensionReason = reason;
    }

    public void Deactivate(string deactivatedBy)
    {
        if (Status is not (MerchantStatus.Active or MerchantStatus.Suspended))
            throw new InvalidOperationException($"Cannot transition from {Status} to Deactivated");

        Status = MerchantStatus.Deactivated;
        DeactivatedAt = DateTimeOffset.UtcNow;
        DeactivatedBy = deactivatedBy;
    }

    public void Reject(string rejectedBy, string reason)
    {
        if (Status != MerchantStatus.Pending)
            throw new InvalidOperationException($"Cannot transition from {Status} to Rejected");

        Status = MerchantStatus.Rejected;
        RejectedAt = DateTimeOffset.UtcNow;
        RejectedBy = rejectedBy;
        RejectionReason = reason;
    }

    public void SetPending()
    {
        if (Status != MerchantStatus.Rejected)
            throw new InvalidOperationException($"Cannot transition from {Status} to Pending");

        Status = MerchantStatus.Pending;
    }

    public void UpdateProfile(
        string? name,
        string? bio,
        string? logoUrl,
        string? description,
        string? contactEmail,
        string? contactPhone,
        string? category,
        string? website)
    {
        if (name is not null) Name = name;
        if (bio is not null) Bio = bio;
        if (logoUrl is not null) LogoUrl = logoUrl;
        if (description is not null) Description = description;
        if (contactEmail is not null) ContactEmail = contactEmail;
        if (contactPhone is not null) ContactPhone = contactPhone;
        if (category is not null) Category = category;
        if (website is not null) Website = website;
    }

    public static MerchantProfile Create(Guid ownerId, string name, string slug)
    {
        return new MerchantProfile
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            Slug = slug,
            Status = MerchantStatus.Pending
        };
    }
}
