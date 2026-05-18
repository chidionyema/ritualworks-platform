using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Application.Sellers.Commands.RegisterSeller;

public record RegisterSellerCommand(Guid SellerId, string Email, string IdempotencyKey = "") : IRequest<Guid>;

public class RegisterSellerCommandHandler : IRequestHandler<RegisterSellerCommand, Guid>
{
    private readonly IPayoutsDbContext _context;
    private readonly IPayoutGateway _payoutGateway;
    private readonly ILogger<RegisterSellerCommandHandler> _logger;

    public RegisterSellerCommandHandler(IPayoutsDbContext context, IPayoutGateway payoutGateway, ILogger<RegisterSellerCommandHandler> logger)
    {
        _context = context;
        _payoutGateway = payoutGateway;
        _logger = logger;
    }

    public async Task<Guid> Handle(RegisterSellerCommand request, CancellationToken cancellationToken)
    {
        var existing = await _context.SellerProfiles
            .FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
        if (existing != null)
        {
            _logger.LogDebug("Seller {SellerId} already registered — returning existing profile", request.SellerId);
            return existing.Id;
        }

        _logger.LogInformation("Registering new seller {SellerId} with Stripe Connect", request.SellerId);
        var externalId = await _payoutGateway.CreateConnectedAccountAsync(request.SellerId, request.Email, cancellationToken);

        var profile = SellerProfile.Create(request.SellerId);
        profile.ExternalProviderId = externalId;
        _context.SellerProfiles.Add(profile);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Seller {SellerId} registered successfully. ProfileId={ProfileId}, StripeAccountId={StripeId}",
                request.SellerId, profile.Id, externalId);
        }
        catch (DbUpdateException)
        {
            _logger.LogWarning(
                "Race condition on seller registration {SellerId} — cleaning up orphaned Stripe account {StripeId}",
                request.SellerId, externalId);
            await _payoutGateway.DeleteConnectedAccountAsync(externalId, cancellationToken);

            var raceWinner = await _context.SellerProfiles
                .FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
            if (raceWinner != null) return raceWinner.Id;
            throw;
        }

        return profile.Id;
    }
}
