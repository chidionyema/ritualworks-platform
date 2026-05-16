using FluentValidation;
using Haworks.Contracts.Merchant;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Domain.Aggregates;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Commands.CreateMerchant;

public record CreateMerchantCommand(Guid OwnerId, string Name, string Slug) : IRequest<Guid>;

public class CreateMerchantCommandValidator : AbstractValidator<CreateMerchantCommand>
{
    public CreateMerchantCommandValidator()
    {
        RuleFor(v => v.OwnerId).NotEmpty();
        RuleFor(v => v.Name).NotEmpty().MaximumLength(200);
        RuleFor(v => v.Slug).NotEmpty().MaximumLength(100).Matches(@"^[a-z0-9-]+$");
    }
}

public class CreateMerchantCommandHandler : IRequestHandler<CreateMerchantCommand, Guid>
{
    private readonly IMerchantDbContext _context;
    private readonly IPublishEndpoint _publishEndpoint;

    public CreateMerchantCommandHandler(IMerchantDbContext context, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Guid> Handle(CreateMerchantCommand request, CancellationToken cancellationToken)
    {
        var existingOwner = await _context.Merchants.AnyAsync(m => m.OwnerId == request.OwnerId, cancellationToken);
        if (existingOwner) throw new InvalidOperationException("Owner already has a merchant.");

        var existingSlug = await _context.Merchants.AnyAsync(m => m.Slug == request.Slug, cancellationToken);
        if (existingSlug) throw new InvalidOperationException("Slug is already in use");

        var merchant = MerchantProfile.Create(request.OwnerId, request.Name, request.Slug);

        _context.Merchants.Add(merchant);
        await _context.SaveChangesAsync(cancellationToken);

        await _publishEndpoint.Publish(new MerchantCreatedEvent
        {
            MerchantId = merchant.Id,
            OwnerId = merchant.OwnerId,
            Name = merchant.Name,
            Slug = merchant.Slug
        }, cancellationToken);

        return merchant.Id;
    }
}
