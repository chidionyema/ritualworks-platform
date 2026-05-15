using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.BuildingBlocks.Testing;
using Haworks.Catalog.UnitTests.Helpers;
using Haworks.Contracts.Catalog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Catalog.UnitTests.Commands.Products;

public class DeleteProductCommandHandlerTests : TestBase
{
    private readonly Mock<IProductRepository> _productRepositoryMock;
    private readonly Mock<IProductCacheReader> _productCacheMock;
    private readonly Mock<IDomainEventPublisher> _eventPublisherMock;
    private readonly DeleteProductCommandHandler _handler;

    public DeleteProductCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _productRepositoryMock = MockRepository.Create<IProductRepository>();
        _productCacheMock = new Mock<IProductCacheReader>();
        _eventPublisherMock = new Mock<IDomainEventPublisher>();
        var loggerMock = new Mock<ILogger<DeleteProductCommandHandler>>();

        _handler = new DeleteProductCommandHandler(
            _productRepositoryMock.Object,
            _productCacheMock.Object,
            _eventPublisherMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRequest_ReturnsSuccess()
    {
        var product = DomainTestHelpers.CreateProduct();
        var command = new DeleteProductCommand(product.Id);

        _productRepositoryMock
            .Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(product);

        _productRepositoryMock
            .Setup(x => x.DeleteAsync(product.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _productRepositoryMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsSuccess);
        _eventPublisherMock.Verify(
            x => x.PublishAsync(
                It.Is<ProductCacheInvalidatedEvent>(e =>
                    e.ProductId == product.Id &&
                    e.Reason == "deleted"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_WithInvalidProduct_ReturnsFailure()
    {
        var command = new DeleteProductCommand(Guid.NewGuid());

        _productRepositoryMock
            .Setup(x => x.GetByIdAsync(command.ProductId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Products.NotFound", result.Error.Code);
    }
}
