using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record UpdateCategoryCommand(Guid CategoryId, string Name) : IRequest<Result<CategoryDto>>;

internal sealed class UpdateCategoryCommandHandler : IRequestHandler<UpdateCategoryCommand, Result<CategoryDto>>
{
    private readonly ICategoryRepository _repository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ILogger<UpdateCategoryCommandHandler> _logger;

    public UpdateCategoryCommandHandler(
        ICategoryRepository repository,
        IDomainEventPublisher eventPublisher,
        ILogger<UpdateCategoryCommandHandler> logger)
    {
        _repository = repository;
        _eventPublisher = eventPublisher;
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

        // Publish before SaveChanges so the outbox row lands in the same TX
        // as the rename. Search-svc relies on this event to re-denormalize
        // the cached categoryName on every product in the affected category.
        await _eventPublisher.PublishAsync(new CategoryUpdatedEvent
        {
            CategoryId = category.Id,
            Name = category.Name,
        }, cancellationToken);

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Concurrency conflict updating category {CategoryId}", request.CategoryId);
            return Result.Failure<CategoryDto>(new Error("Categories.ConcurrencyConflict",
                $"Category {request.CategoryId} was modified by another request. Please retry."));
        }

        _logger.LogInformation("Category updated {CategoryId}", request.CategoryId);

        return Result.Success(new CategoryDto(category.Id, category.Name, category.Description));
    }
}
