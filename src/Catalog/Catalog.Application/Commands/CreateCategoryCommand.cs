using FluentValidation;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record CreateCategoryCommand(string Name, string? Description) : IRequest<Result<Guid>>;

internal sealed class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

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
