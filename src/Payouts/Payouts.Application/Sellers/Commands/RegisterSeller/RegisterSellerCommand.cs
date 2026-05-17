using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Sellers.Commands.RegisterSeller;

public record RegisterSellerCommand(Guid SellerId, string Email) : IRequest<Guid>;

public class RegisterSellerCommandHandler : IRequestHandler<RegisterSellerCommand, Guid>
{
    private readonly IPayoutsDbContext _context;
    private readonly IPayoutGateway _payoutGateway;
    public RegisterSellerCommandHandler(IPayoutsDbContext context, IPayoutGateway payoutGateway) { _context = context; _payoutGateway = payoutGateway; }
    public async Task<Guid> Handle(RegisterSellerCommand request, CancellationToken cancellationToken)
    {
        var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
        if (profile != null) return profile.Id;
        profile = SellerProfile.Create(request.SellerId);
        profile.ExternalProviderId = await _payoutGateway.CreateConnectedAccountAsync(request.SellerId, request.Email);
        _context.SellerProfiles.Add(profile);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Race condition: another thread inserted the same SellerId.
            // Re-query and return the existing profile.
            var existing = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
            if (existing != null) return existing.Id;
            throw; // Unexpected constraint violation — bubble up.
        }
        return profile.Id;
    }
}
