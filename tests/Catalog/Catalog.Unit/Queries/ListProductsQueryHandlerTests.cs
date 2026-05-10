using FluentAssertions;
using Haworks.Catalog.Application.Queries;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Moq;
using Xunit;

namespace Haworks.Catalog.Unit.Queries;

public class ListProductsQueryHandlerTests
{
    private readonly Mock<IProductRepository> _repositoryMock = new();
    private readonly ListProductsQueryHandler _handler;

    public ListProductsQueryHandlerTests()
    {
        _handler = new ListProductsQueryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ReturnsPagedProducts()
    {
        var products = new List<Product> { Product.Create("P1", "D1", 10m, Guid.NewGuid()) };
        _repositoryMock.Setup(r => r.ListAsync(0, 20, null, It.IsAny<CancellationToken>())).ReturnsAsync(products);
        _repositoryMock.Setup(r => r.CountAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _handler.Handle(new ListProductsQuery(0, 20, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Total.Should().Be(1);
    }
}
