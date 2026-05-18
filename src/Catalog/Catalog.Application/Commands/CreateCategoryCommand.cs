using FluentValidation;
using Haworks.BuildingBlocks.Idempotency;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record CreateCategoryCommand(string Name, string? Description, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Guid>>;


internal sealed class CreateCategoryCommandHandler(
    ICategoryRepository categories,
    ILogger<CreateCategoryCommandHandler> logger
) : IRequestHandler<CreateCategoryCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCategoryCommand request, CancellationToken ct)
    {
        var category = Category.Create(request.Name, request.Description);
        await categories.AddAsync(category, ct);
        await categories.SaveChangesAsync(ct);

        logger.LogInformation("Category {CategoryId} ({Name}) created", category.Id, request.Name);
        return Result.Success(category.Id);
    }
}
