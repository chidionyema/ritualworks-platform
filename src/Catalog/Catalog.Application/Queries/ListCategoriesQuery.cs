using MediatR;
using Haworks.Catalog.Application.DTOs;

namespace Haworks.Catalog.Application.Queries;

public sealed record ListCategoriesQuery() : IRequest<Result<IReadOnlyList<CategoryDto>>>;

internal sealed class ListCategoriesQueryHandler(ICategoryRepository categories)
    : IRequestHandler<ListCategoriesQuery, Result<IReadOnlyList<CategoryDto>>>
{
    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(ListCategoriesQuery request, CancellationToken ct)
    {
        var items = await categories.ListAsync(ct);
        IReadOnlyList<CategoryDto> dtos = items
            .Select(c => new CategoryDto(c.Id, c.Name, c.Description))
            .ToList();
        return Result.Success(dtos);
    }
}
