using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Contracts.Merchant;
using Haworks.Merchant.Application.Common.Interfaces;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Commands.DeactivateMerchant;

public record DeactivateMerchantCommand(Guid MerchantId, Guid UserId, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result>;

public sealed class DeactivateMerchantCommandHandler : IRequestHandler<DeactivateMerchantCommand, Result>
{
    private readonly IMerchantDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public DeactivateMerchantCommandHandler(IMerchantDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result> Handle(DeactivateMerchantCommand request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .FirstOrDefaultAsync(m => m.Id == request.MerchantId, cancellationToken);

        if (merchant is null)
            return Result.Failure(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        if (merchant.OwnerId != request.UserId)
            return Result.Failure(Error.Forbidden("Merchant.Forbidden", "You are not authorized to deactivate this merchant."));

        merchant.Deactivate(request.UserId.ToString());
        await _context.SaveChangesAsync(cancellationToken);

        await _publishEndpoint.Publish(new MerchantDeactivatedEvent
        {
            MerchantId = merchant.Id
        }, cancellationToken);

        return Result.Success();
    }
}
