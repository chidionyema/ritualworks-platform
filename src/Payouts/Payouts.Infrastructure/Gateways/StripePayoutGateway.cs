using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace Haworks.Payouts.Infrastructure.Gateways;

public class StripePayoutGateway : IPayoutGateway
{
    private readonly StripeClient _client;

    public StripePayoutGateway(IConfiguration configuration)
    {
        var secretKey = configuration["Stripe:SecretKey"]
            ?? throw new InvalidOperationException("Stripe:SecretKey is required for payouts");
        _client = new StripeClient(secretKey);
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

        var service = new AccountService(_client);
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

        var service = new AccountLinkService(_client);
        var accountLink = await service.CreateAsync(options);
        return accountLink.Url;
    }

    public async Task<(string ExternalId, PayoutStatus Status)> InitiatePayoutAsync(string providerId, decimal amount, string currency, string? description = null, string? idempotencyKey = null)
    {
        var options = new TransferCreateOptions
        {
            Amount = (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero),
            Currency = currency.ToLowerInvariant(),
            Destination = providerId,
            Description = description
        };

        var requestOptions = new RequestOptions
        {
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString()
        };

        var service = new TransferService(_client);
        var transfer = await service.CreateAsync(options, requestOptions);

        return (transfer.Id, PayoutStatus.Succeeded);
    }
}
