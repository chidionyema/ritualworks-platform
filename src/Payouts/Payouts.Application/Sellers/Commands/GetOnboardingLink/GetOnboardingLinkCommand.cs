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
        ValidateRedirectUrl(request.ReturnUrl, nameof(request.ReturnUrl));
        ValidateRedirectUrl(request.RefreshUrl, nameof(request.RefreshUrl));

        var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == request.SellerId, cancellationToken);
        if (profile == null || string.IsNullOrEmpty(profile.ExternalProviderId)) throw new InvalidOperationException("Seller profile not found");
        return await _payoutGateway.CreateAccountOnboardingLinkAsync(profile.ExternalProviderId, request.ReturnUrl, request.RefreshUrl);
    }

    private static void ValidateRedirectUrl(string url, string paramName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL: {paramName}", paramName);
        // M3 Fix: Require HTTPS only (consistent with controller validation)
        if (!string.Equals(uri.Scheme, "https", StringComparison.Ordinal))
            throw new ArgumentException($"URL must use HTTPS: {paramName}", paramName);
        if (uri.Host is "localhost" or "127.0.0.1" or "0.0.0.0")
            throw new ArgumentException($"URL must not point to internal hosts: {paramName}", paramName);
    }
}
