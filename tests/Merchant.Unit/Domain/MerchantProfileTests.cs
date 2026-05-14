using FluentAssertions;
using Haworks.Merchant.Domain.Aggregates;
using Haworks.Merchant.Domain.Enums;
using Xunit;

namespace Haworks.Merchant.Unit.Domain;

public class MerchantProfileTests
{
    [Fact]
    public void Create_Should_Set_Initial_Status_To_Active()
    {
        var ownerId = Guid.NewGuid();
        var merchant = MerchantProfile.Create(ownerId, "Test Shop", "test-shop");

        merchant.Status.Should().Be(MerchantStatus.Active);
        merchant.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public void Suspend_Should_Change_Status_To_Suspended()
    {
        var merchant = MerchantProfile.Create(Guid.NewGuid(), "Shop", "shop");
        merchant.Suspend();

        merchant.Status.Should().Be(MerchantStatus.Suspended);
    }
}
