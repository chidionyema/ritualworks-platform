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

    public async Task<string> CreateConnectedAccountAsync(Guid sellerId, string email, CancellationToken ct = default)
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
        var account = await service.CreateAsync(options, cancellationToken: ct);
        return account.Id;
    }

    public async Task DeleteConnectedAccountAsync(string providerId, CancellationToken ct = default)
    {
        var service = new AccountService(_client);
        await service.DeleteAsync(providerId, cancellationToken: ct);
    }

    public async Task<string> CreateAccountOnboardingLinkAsync(string providerId, string returnUrl, string refreshUrl, CancellationToken ct = default)
    {
        var options = new AccountLinkCreateOptions
        {
            Account = providerId,
            RefreshUrl = refreshUrl,
            ReturnUrl = returnUrl,
            Type = "account_onboarding",
        };

        var service = new AccountLinkService(_client);
        var accountLink = await service.CreateAsync(options, cancellationToken: ct);
        return accountLink.Url;
    }

    public async Task<(string ExternalId, PayoutStatus Status)> InitiatePayoutAsync(string providerId, decimal amount, string currency, string? description = null, string? idempotencyKey = null, CancellationToken ct = default)
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
        var transfer = await service.CreateAsync(options, requestOptions, cancellationToken: ct);

        // C4 Fix: Map Stripe's actual transfer status instead of hardcoding Succeeded.
        // Stripe Transfers are not instant — they go through pending → paid → failed.
        var status = MapStripeStatus(transfer.Reversed, transfer.Id);
        return (transfer.Id, status);
    }

    private static PayoutStatus MapStripeStatus(bool reversed, string transferId)
    {
        // Stripe Transfer objects have limited status fields at creation time.
        // A newly-created transfer is always "pending" until Stripe settles it.
        // The terminal state (paid/failed) arrives via webhook (transfer.paid/transfer.failed).
        if (reversed) return PayoutStatus.Failed;
        return PayoutStatus.InTransit;
    }
}
