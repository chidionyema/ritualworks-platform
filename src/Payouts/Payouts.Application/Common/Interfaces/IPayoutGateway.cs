using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Application.Common.Interfaces;

public interface IPayoutGateway
{
    Task<string> CreateConnectedAccountAsync(Guid sellerId, string email, CancellationToken ct = default);
    Task DeleteConnectedAccountAsync(string providerId, CancellationToken ct = default);
    Task<string> CreateAccountOnboardingLinkAsync(string providerId, string returnUrl, string refreshUrl, CancellationToken ct = default);
    Task<(string ExternalId, PayoutStatus Status)> InitiatePayoutAsync(string providerId, decimal amount, string currency, string? description = null, string? idempotencyKey = null, CancellationToken ct = default);
}
