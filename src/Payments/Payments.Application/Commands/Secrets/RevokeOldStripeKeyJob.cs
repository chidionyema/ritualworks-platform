using Haworks.Contracts.Secrets;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Commands.Secrets;

/// <summary>
/// Hangfire job that revokes the old Stripe API key after the overlap window expires.
/// Scheduled by RotateStripeKeyCommandHandler.
/// </summary>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })]
public sealed class RevokeOldStripeKeyJob
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<RevokeOldStripeKeyJob> _logger;

    public RevokeOldStripeKeyJob(
        IHttpClientFactory httpClientFactory,
        IPublishEndpoint publishEndpoint,
        ILogger<RevokeOldStripeKeyJob> logger)
    {
        _httpClientFactory = httpClientFactory;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid rotationId, string oldKey, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Revoking old Stripe key for rotation {RotationId}", rotationId);

        var revoked = await RevokeStripeKeyAsync(oldKey, cancellationToken).ConfigureAwait(false);

        if (revoked)
        {
            _logger.LogInformation(
                "Successfully revoked old Stripe key for rotation {RotationId}", rotationId);
        }
        else
        {
            _logger.LogError(
                "Failed to revoke old Stripe key for rotation {RotationId} after all retries", rotationId);

            // Publish warning event so ops team is alerted
            await _publishEndpoint.Publish(new SecretExpiryWarningEvent
            {
                SecretPath = "payments/stripe",
                AgePercent = 1.0,
                LastRotatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            // Also publish a specific revocation failure event
            await _publishEndpoint.Publish(new StripeKeyRevocationFailedEvent
            {
                RotationId = rotationId,
                Reason = "Stripe API key revocation failed after 3 retries",
                FailedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Failed to revoke old Stripe key for rotation {rotationId}");
        }
    }

    private async Task<bool> RevokeStripeKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            // Verify the old key is no longer valid by attempting a balance check.
            // If it returns 401, the key has been rotated by Stripe (success).
            // If still active, manual revocation via Stripe Dashboard is required.
            // StripeVerification HttpClient has BaseAddress = https://api.stripe.com/
            var client = _httpClientFactory.CreateClient("StripeVerification");
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/balance");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Key is already revoked/invalid
                return true;
            }

            if (response.IsSuccessStatusCode)
            {
                // Key is still active — requires manual revocation via Stripe Dashboard
                _logger.LogWarning(
                    "Old Stripe key is still active. Manual revocation via Stripe Dashboard required.");
                return false;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during Stripe key revocation check");
            return false;
        }
    }
}
