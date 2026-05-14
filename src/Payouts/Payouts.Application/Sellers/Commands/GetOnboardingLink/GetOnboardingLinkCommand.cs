using Haworks.Payouts.Application.Common.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Sellers.Commands.GetOnboardingLink;

public record GetOnboardingLinkCommand(Guid SellerId, string ReturnUrl, string RefreshUrl) : IRequest<string>;

public class GetOnboardingLinkCommandHandler : IRequestHandler<GetOnboardingLinkCommand, string>
{
    private readonly IPayoutsDbContext _context;
    private readonly IPayoutGateway _payoutGateway;
    public GetOnboardingLinkCommandHandler(IPayoutsDbContext context, IPayoutGateway payoutGateway) { _context = context; _payoutGateway = payoutGateway; }
    public async Task<string> Handle(GetOnboardingLinkCommand request, CancellationToken cancellationToken)
    {
        var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
        if (profile == null || string.IsNullOrEmpty(profile.ExternalProviderId)) throw new InvalidOperationException("Seller profile not found");
        return await _payoutGateway.CreateAccountOnboardingLinkAsync(profile.ExternalProviderId, request.ReturnUrl, request.RefreshUrl);
    }
}
