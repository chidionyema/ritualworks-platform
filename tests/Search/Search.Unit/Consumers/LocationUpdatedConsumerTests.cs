using Haworks.Contracts.Location;
using Haworks.Search.Application.Consumers;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Haworks.Search.Unit.Consumers;

public class LocationUpdatedConsumerTests
{
    private readonly Mock<ILocationSearchIndex> _indexMock;
    private readonly Mock<ILogger<LocationUpdatedConsumer>> _loggerMock;
    private readonly LocationUpdatedConsumer _consumer;

    public LocationUpdatedConsumerTests()
    {
        _indexMock = new Mock<ILocationSearchIndex>();
        _loggerMock = new Mock<ILogger<LocationUpdatedConsumer>>();
        _consumer = new LocationUpdatedConsumer(_indexMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Consume_ShouldUpsertToSearchIndex()
    {
        // Arrange
        var contextMock = new Mock<ConsumeContext<LocationUpdated>>();
        var message = new LocationUpdated
        {
            LocationId = Guid.NewGuid(),
            Address = new AddressInfo
            {
                Street = "123 Test St",
                City = "Test City",
                Postcode = "T1 1ST",
                Country = "Test Country"
            },
            Latitude = 1.23,
            Longitude = 4.56,
            Geohash = "testgeohash",
            Metadata = new Dictionary<string, string> { { "Region", "Test Region" } }
        };
        contextMock.Setup(x => x.Message).Returns(message);
        contextMock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        // Act
        await _consumer.Consume(contextMock.Object);

        // Assert
        _indexMock.Verify(x => x.UpsertAsync(
            It.Is<LocationSearchDocument>(d => 
                d.LocationId == message.LocationId.ToString() &&
                d.Location.Lat == message.Latitude &&
                d.Location.Lon == message.Longitude &&
                d.Postcode == message.Address.Postcode &&
                d.Metadata["Region"] == "Test Region"), 
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
