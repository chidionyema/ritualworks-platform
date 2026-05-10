using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Testing;
using Haworks.Catalog.Application.Commands.Reservations;
using Haworks.Catalog.Application.DTOs.Reservations;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Catalog.UnitTests.Commands.Reservations;

public sealed class CreateReservationHandlerTests : TestBase
{
    private readonly Mock<IProductRepository> _products;
    private readonly CreateReservationCommandHandler _handler;

    public CreateReservationHandlerTests(ITestOutputHelper output) : base(output)
    {
        _products = MockRepository.Create<IProductRepository>();
        _handler = new CreateReservationCommandHandler(
            _products.Object,
            new Mock<ILogger<CreateReservationCommandHandler>>().Object);
    }

    [Fact]
    public async Task Handle_returns_success_dto_when_repository_creates_reservation()
    {
        var productId = Guid.NewGuid();
        var items = new List<ReservationItemDto>
        {
            new(productId, "Widget", 2),
        };

        var reservation = StockReservation.Create("u1", "[]", TimeSpan.FromMinutes(15));

        _products
            .Setup(x => x.CreateReservationAsync(
                "u1",
                It.IsAny<IReadOnlyList<StockReservationItem>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        var result = await _handler.Handle(
            new CreateReservationCommand(items, "u1", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(reservation.Id, result.Value.ReservationId);
        Assert.False(result.Value.IsExisting);
        Assert.Single(result.Value.Items);
    }

    [Fact]
    public async Task Handle_maps_InsufficientStockException_to_Conflict()
    {
        var productId = Guid.NewGuid();
        var items = new List<ReservationItemDto>
        {
            new(productId, "Widget", 50),
        };

        _products
            .Setup(x => x.CreateReservationAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StockReservationItem>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientStockException(productId, 50, 5));

        var result = await _handler.Handle(
            new CreateReservationCommand(items, "u1", null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Reservation.InsufficientStock", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task Handle_passes_through_user_id_to_repository()
    {
        var productId = Guid.NewGuid();
        var items = new List<ReservationItemDto>
        {
            new(productId, "Widget", 1),
        };

        var reservation = StockReservation.Create("alice", "[]", TimeSpan.FromMinutes(15));

        _products
            .Setup(x => x.CreateReservationAsync(
                "alice",
                It.IsAny<IReadOnlyList<StockReservationItem>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation)
            .Verifiable();

        var result = await _handler.Handle(
            new CreateReservationCommand(items, "alice", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        _products.Verify();
    }

    [Fact]
    public async Task Handle_uses_15_minute_ttl()
    {
        var productId = Guid.NewGuid();
        var items = new List<ReservationItemDto>
        {
            new(productId, "Widget", 1),
        };

        TimeSpan? capturedTtl = null;
        _products
            .Setup(x => x.CreateReservationAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StockReservationItem>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<StockReservationItem>, TimeSpan, CancellationToken>(
                (_, _, ttl, _) => capturedTtl = ttl)
            .ReturnsAsync(StockReservation.Create("u1", "[]", TimeSpan.FromMinutes(15)));

        var result = await _handler.Handle(
            new CreateReservationCommand(items, "u1", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromMinutes(15), capturedTtl);
    }
}
