using FluentAssertions;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Location;
using Haworks.Location.Application.Commands;
using Haworks.Location.Application.Interfaces;
using Haworks.Location.Domain.Entities;
using Moq;
using NetTopologySuite.Geometries;
using Xunit;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Haworks.Location.Unit.Commands;

public class CreateAddressCommandHandlerTests
{
    private readonly Mock<ILocationDbContext> _dbContextMock;
    private readonly Mock<IDomainEventPublisher> _publisherMock;
    private readonly Mock<IGeocodingService> _geocodingMock;
    private readonly Mock<IGeohashService> _geohashMock;
    private readonly CreateAddressCommandHandler _handler;

    public CreateAddressCommandHandlerTests()
    {
        _dbContextMock = new Mock<ILocationDbContext>();
        _publisherMock = new Mock<IDomainEventPublisher>();
        _geocodingMock = new Mock<IGeocodingService>();
        _geohashMock = new Mock<IGeohashService>();
        
        // Mock DbSet
        var addresses = new List<Address>();
        var dbSetMock = CreateMockDbSet(addresses);
        _dbContextMock.Setup(x => x.Addresses).Returns(dbSetMock.Object);

        _handler = new CreateAddressCommandHandler(
            _dbContextMock.Object, 
            _publisherMock.Object,
            _geocodingMock.Object,
            _geohashMock.Object);
    }

    private static Mock<DbSet<T>> CreateMockDbSet<T>(List<T> sourceList) where T : class
    {
        var mockSet = new Mock<DbSet<T>>();
        mockSet.As<IQueryable<T>>().Setup(m => m.Provider).Returns(sourceList.AsQueryable().Provider);
        mockSet.As<IQueryable<T>>().Setup(m => m.Expression).Returns(sourceList.AsQueryable().Expression);
        mockSet.As<IQueryable<T>>().Setup(m => m.ElementType).Returns(sourceList.AsQueryable().ElementType);
        mockSet.As<IQueryable<T>>().Setup(m => m.GetEnumerator()).Returns(sourceList.AsQueryable().GetEnumerator());
        mockSet.Setup(d => d.Add(It.IsAny<T>())).Callback<T>(sourceList.Add);
        return mockSet;
    }

    [Fact]
    public async Task Handle_WithCoords_ShouldSaveAddressAndPublishEvent()
    {
        // Arrange
        var command = new CreateAddressCommand
        {
            Street = "123 Main St",
            City = "London",
            Postcode = "SW1A 1AA",
            Country = "United Kingdom",
            Latitude = 51.5074,
            Longitude = -0.1278
        };
        _geohashMock.Setup(x => x.Encode(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns("gcpvj0d9");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        _dbContextMock.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _geocodingMock.Verify(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _geohashMock.Verify(x => x.Encode(command.Latitude.Value, command.Longitude.Value, 12), Times.Once);
    }

    [Fact]
    public async Task Handle_WithoutCoords_ShouldGeocodeAndSave()
    {
        // Arrange
        var command = new CreateAddressCommand
        {
            Street = "Buckingham Palace",
            City = "London",
            Postcode = "SW1A 1AA",
            Country = "UK"
        };
        _geocodingMock.Setup(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((51.5014, -0.1419));
        _geohashMock.Setup(x => x.Encode(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns("gcpvj0d9");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _geocodingMock.Verify(x => x.GeocodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _geohashMock.Verify(x => x.Encode(51.5014, -0.1419, 12), Times.Once);
    }
}
