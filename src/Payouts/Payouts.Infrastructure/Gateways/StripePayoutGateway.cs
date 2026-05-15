using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace Haworks.Payouts.Infrastructure.Gateways;

public class StripePayoutGateway : IPayoutGateway
{
    private readonly IConfiguration _configuration;

    public StripePayoutGateway(IConfiguration configuration)
    {
        _configuration = configuration;
        StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
    }

    public async Task<string> CreateConnectedAccountAsync(Guid sellerId, string email)
    {
        var options = new AccountCreateOptions
        {
            Type = "express",
            Email = email,
            Metadata = new Dictionary<string, string>
            {
                { "SellerId", sellerId.ToString() }
            }
        };

        var service = new AccountService();
        var account = await service.CreateAsync(options);
        return account.Id;
    }

    public async Task<string> CreateAccountOnboardingLinkAsync(string providerId, string returnUrl, string refreshUrl)
    {
        var options = new AccountLinkCreateOptions
        {
            Account = providerId,
            RefreshUrl = refreshUrl,
            ReturnUrl = returnUrl,
            Type = "account_onboarding",
        };

        var service = new AccountLinkService();
        var accountLink = await service.CreateAsync(options);
        return accountLink.Url;
    }

    public async Task<(string ExternalId, PayoutStatus Status)> InitiatePayoutAsync(string providerId, decimal amount, string currency, string? description = null)
    {
        var options = new TransferCreateOptions
        {
            Amount = (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero),
            Currency = currency.ToLowerInvariant(),
            Destination = providerId,
            Description = description
        };

        var service = new TransferService();
        var transfer = await service.CreateAsync(options);

        return (transfer.Id, PayoutStatus.Succeeded);
    }
}
