using FluentAssertions;
using Haworks.Merchant.Domain.Aggregates;
using Haworks.Merchant.Domain.Enums;
using Xunit;

namespace Haworks.Merchant.Unit.Domain;

public sealed class MerchantProfileTests
{
    [Fact]
    public void Create_sets_status_to_Active()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test Shop", "test-shop");

        profile.Status.Should().Be(MerchantStatus.Active);
        profile.Name.Should().Be("Test Shop");
        profile.Slug.Should().Be("test-shop");
        profile.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Suspend_changes_status_to_Suspended()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");

        profile.Suspend();

        profile.Status.Should().Be(MerchantStatus.Suspended);
    }

    [Fact]
    public void Activate_changes_status_to_Active()
    {
        var profile = MerchantProfile.Create(Guid.NewGuid(), "Test", "test");
        profile.Suspend();

        profile.Activate();

        profile.Status.Should().Be(MerchantStatus.Active);
    }
}
