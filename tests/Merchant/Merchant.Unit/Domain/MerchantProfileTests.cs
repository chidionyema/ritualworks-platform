using FluentAssertions;
using Haworks.Merchant.Domain.Aggregates;
using Haworks.Merchant.Domain.Enums;
using Xunit;

namespace Haworks.Merchant.Unit.Domain;

public sealed class MerchantProfileTests
{
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

        profile.Activate();

        profile.Status.Should().Be(MerchantStatus.Active);
    }

    [Fact]
    public void Activate_from_Suspended_succeeds()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate();
        profile.Suspend();

        profile.Activate();

        profile.Status.Should().Be(MerchantStatus.Active);
    }

    [Fact]
    public void Activate_from_Rejected_throws()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Reject("policy violation");

        var act = () => profile.Activate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*rejected*");
    }

    [Fact]
    public void Suspend_changes_status_to_Suspended()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Suspend();

        profile.Status.Should().Be(MerchantStatus.Suspended);
    }

    [Fact]
    public void Deactivate_changes_status_to_Deactivated()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Deactivate();

        profile.Status.Should().Be(MerchantStatus.Deactivated);
    }

    [Fact]
    public void Reject_sets_status_and_reason()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Reject("Terms violation");

        profile.Status.Should().Be(MerchantStatus.Rejected);
        profile.RejectionReason.Should().Be("Terms violation");
    }

    [Fact]
    public void SetPending_changes_status_to_Pending()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Activate();

        profile.SetPending();

        profile.Status.Should().Be(MerchantStatus.Pending);
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
