using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.DTOs;
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

public class UpdateProductCommandHandlerTests : TestBase
{
    private readonly Mock<IProductRepository> _productRepositoryMock;
    private readonly Mock<ICategoryRepository> _categoryRepositoryMock;
    private readonly Mock<IProductCacheReader> _productCacheMock;
    private readonly Mock<IDomainEventPublisher> _eventPublisherMock;
    private readonly UpdateProductCommandHandler _handler;

    public UpdateProductCommandHandlerTests(ITestOutputHelper output) : base(output)
    {
        _productRepositoryMock = MockRepository.Create<IProductRepository>();
        _categoryRepositoryMock = MockRepository.Create<ICategoryRepository>();
        _productCacheMock = new Mock<IProductCacheReader>();
        _eventPublisherMock = new Mock<IDomainEventPublisher>();

        var loggerMock = new Mock<ILogger<UpdateProductCommandHandler>>();

        _handler = new UpdateProductCommandHandler(
            _productRepositoryMock.Object,
            _categoryRepositoryMock.Object,
            _productCacheMock.Object,
            _eventPublisherMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRequest_ReturnsSuccessAndUpdatesProduct()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var product = DomainTestHelpers.CreateProduct(categoryId: categoryId);
        var category = DomainTestHelpers.CreateCategory();
        
        var command = new UpdateProductCommand(
            product.Id,
            "New Name",
            "New Description",
            20.99m,
            categoryId,
            true);

        _productRepositoryMock
            .Setup(x => x.GetByIdTrackedAsync(product.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(product);

        _categoryRepositoryMock
            .Setup(x => x.GetByIdAsync(categoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(category);

        _productRepositoryMock
            .Setup(x => x.UpdateAsync(It.Is<Product>(p => p.Name == "New Name"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _productRepositoryMock
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("New Name", result.Value.Name);
        Assert.True(product.IsListed);
        _eventPublisherMock.Verify(
            x => x.PublishAsync(
                It.Is<ProductCacheInvalidatedEvent>(e =>
                    e.ProductId == product.Id &&
                    e.Reason == "updated"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_WithInvalidProduct_ReturnsFailure()
    {
        var command = new UpdateProductCommand(
            Guid.NewGuid(), "Name", "Desc", 10m, Guid.NewGuid(), true);

        _productRepositoryMock
            .Setup(x => x.GetByIdTrackedAsync(command.ProductId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Products.NotFound", result.Error.Code);
    }
}
