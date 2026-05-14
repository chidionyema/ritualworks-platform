using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Application.Common.Interfaces;

public interface IPayoutGateway
{
    Task<string> CreateConnectedAccountAsync(Guid sellerId, string email);
    Task<string> CreateAccountOnboardingLinkAsync(string providerId, string returnUrl, string refreshUrl);
    Task<(string ExternalId, PayoutStatus Status)> InitiatePayoutAsync(string providerId, decimal amount, string currency, string? description = null);
}
