using FluentValidation;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Merchant.Application.Common.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Commands.UpdateMerchant;

public record UpdateMerchantCommand(
    Guid MerchantId,
    Guid UserId,
    string? Name,
    string? Bio,
    string? LogoUrl,
    string? Description,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    string? Website,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result>;

public class UpdateMerchantCommandValidator : AbstractValidator<UpdateMerchantCommand>
{
    public UpdateMerchantCommandValidator()
    {
        RuleFor(v => v.MerchantId).NotEmpty();
        RuleFor(v => v.Name).MaximumLength(200).When(v => v.Name is not null);
        RuleFor(v => v.Bio).MaximumLength(2000).When(v => v.Bio is not null);
        RuleFor(v => v.Description).MaximumLength(2000).When(v => v.Description is not null);
        RuleFor(v => v.ContactEmail).EmailAddress().When(v => v.ContactEmail is not null);
        RuleFor(v => v.LogoUrl).Must(BeAValidUri).When(v => v.LogoUrl is not null).WithMessage("Invalid URL format.");
        RuleFor(v => v.Website).Must(BeAValidUri).When(v => v.Website is not null).WithMessage("Invalid URL format.");
    }

    private static bool BeAValidUri(string? url) =>
        url is null || Uri.TryCreate(url, UriKind.Absolute, out _);
}

public sealed class UpdateMerchantCommandHandler : IRequestHandler<UpdateMerchantCommand, Result>
{
    private readonly IMerchantDbContext _context;

    public UpdateMerchantCommandHandler(IMerchantDbContext context) => _context = context;

    public async Task<Result> Handle(UpdateMerchantCommand request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .FirstOrDefaultAsync(m => m.Id == request.MerchantId, cancellationToken);

        if (merchant is null)
            return Result.Failure(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        if (merchant.OwnerId != request.UserId)
            return Result.Failure(Error.Forbidden("Merchant.Forbidden", "You are not authorized to update this merchant."));

        merchant.UpdateProfile(
            request.Name, request.Bio, request.LogoUrl, request.Description,
            request.ContactEmail, request.ContactPhone, request.Category, request.Website);

        await _context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
