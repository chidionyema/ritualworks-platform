using FluentAssertions;
using Haworks.Merchant.Domain.Aggregates;
using Haworks.Merchant.Domain.Enums;
using Xunit;

namespace Haworks.Merchant.Unit.Domain;

public sealed class MerchantProfileTests
{
    private const string AdminId = "admin-user-123";

    [Fact]
    public void Create_sets_status_to_Pending()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test Shop", "test-shop");

        profile.Status.Should().Be(MerchantStatus.Pending);
        profile.Name.Should().Be("Test Shop");
        profile.Slug.Should().Be("test-shop");
        profile.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Activate_from_Pending_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Activate(AdminId);

        profile.Status.Should().Be(MerchantStatus.Active);
        profile.ApprovedBy.Should().Be(AdminId);
        profile.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public void Activate_from_Suspended_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);
        profile.Suspend(AdminId, "review");

        profile.Activate(AdminId);

        profile.Status.Should().Be(MerchantStatus.Active);
    }

    [Fact]
    public void Activate_from_Rejected_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Reject(AdminId, "policy violation");

        var act = () => profile.Activate(AdminId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Rejected*Active*");
    }

    [Fact]
    public void Activate_from_Deactivated_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);
        profile.Deactivate(AdminId);

        var act = () => profile.Activate(AdminId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Deactivated*Active*");
    }

    [Fact]
    public void Suspend_from_Active_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);

        profile.Suspend(AdminId, "Terms violation");

        profile.Status.Should().Be(MerchantStatus.Suspended);
        profile.SuspendedBy.Should().Be(AdminId);
        profile.SuspendedAt.Should().NotBeNull();
        profile.SuspensionReason.Should().Be("Terms violation");
    }

    [Fact]
    public void Suspend_from_Pending_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        var act = () => profile.Suspend(AdminId, "reason");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Pending*Suspended*");
    }

    [Fact]
    public void Suspend_from_Deactivated_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);
        profile.Deactivate(AdminId);

        var act = () => profile.Suspend(AdminId, "reason");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Deactivated*Suspended*");
    }

    [Fact]
    public void Deactivate_from_Active_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);

        profile.Deactivate(AdminId);

        profile.Status.Should().Be(MerchantStatus.Deactivated);
        profile.DeactivatedBy.Should().Be(AdminId);
        profile.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public void Deactivate_from_Suspended_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);
        profile.Suspend(AdminId, "review");

        profile.Deactivate(AdminId);

        profile.Status.Should().Be(MerchantStatus.Deactivated);
    }

    [Fact]
    public void Deactivate_from_Pending_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        var act = () => profile.Deactivate(AdminId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Pending*Deactivated*");
    }

    [Fact]
    public void Deactivate_from_Rejected_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Reject(AdminId, "bad");

        var act = () => profile.Deactivate(AdminId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Rejected*Deactivated*");
    }

    [Fact]
    public void Reject_from_Pending_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Reject(AdminId, "Terms violation");

        profile.Status.Should().Be(MerchantStatus.Rejected);
        profile.RejectionReason.Should().Be("Terms violation");
        profile.RejectedBy.Should().Be(AdminId);
        profile.RejectedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reject_from_Active_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);

        var act = () => profile.Reject(AdminId, "reason");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Active*Rejected*");
    }

    [Fact]
    public void Reject_from_Suspended_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);
        profile.Suspend(AdminId, "review");

        var act = () => profile.Reject(AdminId, "reason");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Suspended*Rejected*");
    }

    [Fact]
    public void SetPending_from_Rejected_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Reject(AdminId, "policy violation");

        profile.SetPending();

        profile.Status.Should().Be(MerchantStatus.Pending);
    }

    [Fact]
    public void SetPending_from_Active_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate(AdminId);

        var act = () => profile.SetPending();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Active*Pending*");
    }

    [Fact]
    public void SetPending_from_Pending_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        var act = () => profile.SetPending();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Pending*Pending*");
    }

    [Fact]
    public void UpdateProfile_sets_provided_fields()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Original", "original");

        profile.UpdateProfile("New Name", "New Bio", "https://logo.png", "Desc",
            "test@example.com", "+1234567890", "Food", "https://site.com");

        profile.Name.Should().Be("New Name");
        profile.Bio.Should().Be("New Bio");
        profile.LogoUrl.Should().Be("https://logo.png");
        profile.Description.Should().Be("Desc");
        profile.ContactEmail.Should().Be("test@example.com");
        profile.ContactPhone.Should().Be("+1234567890");
        profile.Category.Should().Be("Food");
        profile.Website.Should().Be("https://site.com");
    }

    [Fact]
    public void UpdateProfile_ignores_null_fields()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Original", "original");
        profile.UpdateProfile("First", null, null, null, null, null, null, null);

        profile.UpdateProfile(null, "Updated Bio", null, null, null, null, null, null);

        profile.Name.Should().Be("First");
        profile.Bio.Should().Be("Updated Bio");
    }
}
