using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record UpdateCategoryCommand(Guid CategoryId, string Name) : IRequest<Result<CategoryDto>>;

internal sealed class UpdateCategoryCommandHandler : IRequestHandler<UpdateCategoryCommand, Result<CategoryDto>>
{
    private readonly ICategoryRepository _repository;
    private readonly ILogger<UpdateCategoryCommandHandler> _logger;

    public UpdateCategoryCommandHandler(
        ICategoryRepository repository,
        ILogger<UpdateCategoryCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result<CategoryDto>> Handle(
        UpdateCategoryCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Result.Failure<CategoryDto>(new Error("Categories.InvalidName", "Category name cannot be empty."));
        }

        _logger.LogInformation("Updating category {CategoryId}", request.CategoryId);

        var category = await _repository.GetByIdAsync(request.CategoryId, cancellationToken);

        if (category == null)
        {
            return Result.Failure<CategoryDto>(new Error("Categories.NotFound", $"Category {request.CategoryId} not found"));
        }

        category.Rename(request.Name);
        await _repository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Category updated {CategoryId}", request.CategoryId);

        return Result.Success(new CategoryDto(category.Id, category.Name, category.Description));
    }
}
