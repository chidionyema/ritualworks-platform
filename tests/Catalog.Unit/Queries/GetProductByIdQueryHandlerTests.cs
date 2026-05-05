using FluentAssertions;
using Haworks.Catalog.Application.Queries;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Moq;
using Xunit;

namespace Haworks.Catalog.Unit.Queries;

public class GetProductByIdQueryHandlerTests
{
    private readonly Mock<IProductRepository> _repositoryMock = new();
    private readonly GetProductByIdQueryHandler _handler;

    public GetProductByIdQueryHandlerTests()
    {
        _handler = new GetProductByIdQueryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_WhenProductExists_ReturnsSuccess()
    {
        var productId = Guid.NewGuid();
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        _repositoryMock.Setup(r => r.GetByIdAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var result = await _handler.Handle(new GetProductByIdQuery(productId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Test");
    }

    [Fact]
    public async Task Handle_WhenNotFound_ReturnsFailure()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Product?)null);
        var result = await _handler.Handle(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
    }
}
