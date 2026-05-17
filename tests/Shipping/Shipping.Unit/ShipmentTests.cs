using FluentAssertions;
using Haworks.Shipping.Api.Domain;
using Xunit;

namespace Haworks.Shipping.Unit;

public class ShipmentTests
{
    [Fact]
    public void Create_SetsStatusToPending()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "ep_test123");

        shipment.Status.Should().Be(ShipmentStatus.Created);
        shipment.EasyPostShipmentId.Should().Be("ep_test123");
        shipment.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void MarkLabelPurchased_TransitionsToLabelPurchased()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "ep_test");

        shipment.MarkLabelPurchased("usps", "Priority", "9400111899223", "https://track.example.com", "https://label.example.com/pdf", 7.95m, "USD", DateTime.UtcNow.AddDays(3));

        shipment.Status.Should().Be(ShipmentStatus.LabelPurchased);
        shipment.CarrierCode.Should().Be("usps");
        shipment.TrackingNumber.Should().Be("9400111899223");
        shipment.RateAmount.Should().Be(7.95m);
        shipment.ShippedAt.Should().NotBeNull();
    }

    [Fact]
    public void UpdateStatus_ToDelivered_SetsDeliveredAt()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "ep_test");
        shipment.MarkLabelPurchased("ups", "Ground", "1Z999", "url", "label", 12.50m, "USD", null);

        shipment.UpdateStatus(ShipmentStatus.Delivered);

        shipment.Status.Should().Be(ShipmentStatus.Delivered);
        shipment.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public void UpdateStatus_ToInTransit_DoesNotSetDeliveredAt()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "ep_test");

        shipment.UpdateStatus(ShipmentStatus.InTransit);

        shipment.Status.Should().Be(ShipmentStatus.InTransit);
        shipment.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public void SetAddresses_StoresFromAndTo()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), "ep_test");

        shipment.SetAddresses("123 Main", "NYC", "NY", "10001", "US", "456 Oak", "LA", "CA", "90001", "US");

        shipment.FromStreet.Should().Be("123 Main");
        shipment.ToCity.Should().Be("LA");
        shipment.ToCountry.Should().Be("US");
    }
}
