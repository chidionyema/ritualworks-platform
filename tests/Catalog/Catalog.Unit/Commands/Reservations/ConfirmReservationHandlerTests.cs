using System.Text.Json;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Testing;
using Haworks.Catalog.Application.Commands.Reservations;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Catalog.UnitTests.Commands.Reservations;

public sealed class ConfirmReservationHandlerTests : TestBase
{
    private readonly Mock<IProductRepository> _products;
    private readonly Mock<IDomainEventPublisher> _publisher;
    private readonly ConfirmReservationCommandHandler _handler;

    public ConfirmReservationHandlerTests(ITestOutputHelper output) : base(output)
    {
        _products = MockRepository.Create<IProductRepository>();
        _publisher = MockRepository.Create<IDomainEventPublisher>();
        _handler = new ConfirmReservationCommandHandler(
            _products.Object,
            _publisher.Object,
            new Mock<ILogger<ConfirmReservationCommandHandler>>().Object);
    }

    [Fact]
    public async Task Handle_returns_NotFound_when_reservation_missing()
    {
        var id = Guid.NewGuid();
        _products
            .Setup(x => x.GetReservationByIdTrackedAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StockReservation?)null);

        var result = await _handler.Handle(
            new ConfirmReservationCommand(id, "u1", "u1@x.com", 10m, "USD", null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Reservation.NotFound", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task Handle_returns_Expired_when_pending_but_past_ttl()
    {
        // ttl = -1ms → ExpiresAt is already in the past, Status is Pending.
        var reservation = StockReservation.Create("u1", "[]", TimeSpan.FromMilliseconds(-1));
        _products
            .Setup(x => x.GetReservationByIdTrackedAsync(reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        var result = await _handler.Handle(
            new ConfirmReservationCommand(reservation.Id, "u1", "u1@x.com", 10m, "USD", null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Reservation.Expired", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task Handle_returns_InvalidState_when_already_confirmed()
    {
        // CreateConfirmed jumps straight to Confirmed; Confirm() returns
        // false because Status != Pending.
        var reservation = StockReservation.CreateConfirmed(Guid.NewGuid(), Guid.NewGuid(), "u1", "[]");
        _products
            .Setup(x => x.GetReservationByIdTrackedAsync(reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        var result = await _handler.Handle(
            new ConfirmReservationCommand(reservation.Id, "u1", "u1@x.com", 10m, "USD", null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Reservation.InvalidState", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task Handle_publishes_event_and_returns_orderId_on_success()
    {
        var item = new StockReservationItem
        {
            ProductId = Guid.NewGuid(),
            ProductName = "Widget",
            Quantity = 2,
            RemainingStock = 8,
        };
        var reservation = StockReservation.Create(
            "u1",
            JsonSerializer.Serialize(new[] { item }),
            TimeSpan.FromMinutes(15));

        _products
            .Setup(x => x.GetReservationByIdTrackedAsync(reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);
        _products
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        StockReservedEvent? published = null;
        _publisher
            .Setup(x => x.PublishAsync(It.IsAny<StockReservedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<StockReservedEvent, CancellationToken>((e, _) => published = e)
            .Returns(Task.CompletedTask);

        var result = await _handler.Handle(
            new ConfirmReservationCommand(reservation.Id, "u1", "u1@x.com", 30m, "USD", "key-1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(reservation.Id, result.Value.ReservationId);
        Assert.NotEqual(Guid.Empty, result.Value.OrderId);
        Assert.NotEqual(Guid.Empty, result.Value.SagaId);

        Assert.NotNull(published);
        Assert.Equal(result.Value.OrderId, published!.OrderId);
        Assert.Equal(result.Value.SagaId, published.SagaId);
        Assert.Equal("u1@x.com", published.CustomerEmail);
        Assert.Single(published.Items);
        Assert.Equal(item.ProductId, published.Items[0].ProductId);
    }
}
