using FluentAssertions;
using Haworks.Catalog.Application.Queries;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Moq;
using Xunit;

namespace Haworks.Catalog.Unit.Queries;

public class ListCategoriesQueryHandlerTests
{
    private readonly Mock<ICategoryRepository> _repositoryMock = new();
    private readonly ListCategoriesQueryHandler _handler;

    public ListCategoriesQueryHandlerTests()
    {
        _handler = new ListCategoriesQueryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ReturnsAllCategories()
    {
        var categories = new List<Category> { Category.Create("C1", "D1") };
        _repositoryMock.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(categories);

        var result = await _handler.Handle(new ListCategoriesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Name.Should().Be("C1");
    }
}
