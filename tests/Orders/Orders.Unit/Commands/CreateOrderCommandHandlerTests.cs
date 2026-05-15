using Haworks.Orders.Application.Commands;
using Haworks.Orders.Domain;
using Haworks.Orders.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Orders;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit.Abstractions;
using Xunit;

namespace Haworks.Orders.UnitTests.Commands;

public class CreateOrderCommandHandlerTests : TestBase
{
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IDomainEventPublisher> _eventPublisherMock;
    private readonly CreateOrderCommandHandler _handler;

    public CreateOrderCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _orderRepositoryMock = MockRepository.Create<IOrderRepository>();
        _eventPublisherMock = MockRepository.Create<IDomainEventPublisher>();
        var loggerMock = new Mock<ILogger<CreateOrderCommandHandler>>();

        _handler = new CreateOrderCommandHandler(
            _orderRepositoryMock.Object,
            _eventPublisherMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRequest_CreatesOrderAndPublishesEvent()
    {
        // Arrange
        var command = new CreateOrderCommand(
            Guid.NewGuid().ToString(),
            "test@example.com",
            100m,
            "USD",
            Guid.NewGuid(),
            "idempotency-key",
            new List<CreateOrderLineItem>
            {
                new(Guid.NewGuid(), "Product 1", 1, 100m)
            });

        _orderRepositoryMock
            .Setup(x => x.GetBySagaIdTrackedAsync(command.SagaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        _orderRepositoryMock
            .Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _eventPublisherMock
            .Setup(x => x.PublishAsync(It.IsAny<OrderCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _orderRepositoryMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        _orderRepositoryMock.Verify(
            x => x.AddAsync(
                It.Is<Order>(o =>
                    o.UserId == command.UserId &&
                    o.SagaId == command.SagaId &&
                    o.IdempotencyKey == command.IdempotencyKey &&
                    o.CustomerEmail == command.CustomerEmail &&
                    o.TotalAmount == command.TotalAmount &&
                    o.Items.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _eventPublisherMock.Verify(
            x => x.PublishAsync(
                It.Is<OrderCreatedEvent>(e =>
                    e.CustomerEmail == command.CustomerEmail &&
                    e.TotalAmount == command.TotalAmount),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
