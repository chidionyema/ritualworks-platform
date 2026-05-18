using FluentValidation;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Contracts.Merchant;
using Haworks.Merchant.Application.Common.Interfaces;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Commands.SuspendMerchant;

public record SuspendMerchantCommand(Guid MerchantId, string SuspendedBy, string Reason, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result>;

public class SuspendMerchantCommandValidator : AbstractValidator<SuspendMerchantCommand>
{
    public SuspendMerchantCommandValidator()
    {
        RuleFor(v => v.MerchantId).NotEmpty();
        RuleFor(v => v.SuspendedBy).NotEmpty();
        RuleFor(v => v.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SuspendMerchantCommandHandler : IRequestHandler<SuspendMerchantCommand, Result>
{
    private readonly IMerchantDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public SuspendMerchantCommandHandler(IMerchantDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result> Handle(SuspendMerchantCommand request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .FirstOrDefaultAsync(m => m.Id == request.MerchantId, cancellationToken);

        if (merchant is null)
            return Result.Failure(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        merchant.Suspend(request.SuspendedBy, request.Reason);
        await _context.SaveChangesAsync(cancellationToken);

        await _publishEndpoint.Publish(new MerchantSuspendedEvent
        {
            MerchantId = merchant.Id
        }, cancellationToken);

        return Result.Success();
    }
}
