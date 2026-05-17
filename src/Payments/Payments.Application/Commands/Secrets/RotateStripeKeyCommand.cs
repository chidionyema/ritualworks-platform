using Haworks.Contracts.Secrets;
using Hangfire;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VaultSharp;

namespace Haworks.Payments.Application.Commands.Secrets;

/// <summary>
/// Command to rotate the Stripe API key. Admin-only.
/// </summary>
public sealed record RotateStripeKeyCommand : IRequest<RotateStripeKeyResult>
{
    public required string NewSecretKey { get; init; }
}

public sealed record RotateStripeKeyResult
{
    public required Guid RotationId { get; init; }
    public required DateTimeOffset OverlapExpiresAt { get; init; }
}

public sealed class RotateStripeKeyCommandHandler : IRequestHandler<RotateStripeKeyCommand, RotateStripeKeyResult>
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RotateStripeKeyCommandHandler> _logger;
    private readonly IVaultClient _vaultClient;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IHttpClientFactory _httpClientFactory;

    private const string VaultSecretPath = "payments/stripe";

    public RotateStripeKeyCommandHandler(
        IPublishEndpoint publishEndpoint,
        IConfiguration configuration,
        ILogger<RotateStripeKeyCommandHandler> logger,
        IVaultClient vaultClient,
        IBackgroundJobClient backgroundJobClient,
        IHttpClientFactory httpClientFactory)
    {
        _publishEndpoint = publishEndpoint;
        _configuration = configuration;
        _logger = logger;
        _vaultClient = vaultClient;
        _backgroundJobClient = backgroundJobClient;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<RotateStripeKeyResult> Handle(
        RotateStripeKeyCommand request, CancellationToken cancellationToken)
    {
        // Validate key format
        if (string.IsNullOrWhiteSpace(request.NewSecretKey) ||
            (!request.NewSecretKey.StartsWith("sk_live_", StringComparison.Ordinal) &&
             !request.NewSecretKey.StartsWith("sk_test_", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Invalid Stripe key format. Must start with 'sk_live_' or 'sk_test_'.",
                nameof(request));
        }

        var rotationId = Guid.NewGuid();
        var overlapHours = _configuration.GetValue("Stripe:OverlapHours", 24);
        var overlapExpiresAt = DateTimeOffset.UtcNow.AddHours(overlapHours);

        // Step 1: Read old key from Vault
        string? oldKey = null;
        try
        {
            var existingSecret = await _vaultClient.V1.Secrets.KeyValue.V2
                .ReadSecretAsync(VaultSecretPath, mountPoint: "secret")
                .ConfigureAwait(false);

            if (existingSecret?.Data?.Data != null &&
                existingSecret.Data.Data.TryGetValue("SecretKey", out var oldKeyObj))
            {
                oldKey = oldKeyObj?.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read existing Stripe key from Vault for rotation {RotationId}. Proceeding with write-only.",
                rotationId);
        }

        // Step 2: Write new key to Vault
        await _vaultClient.V1.Secrets.KeyValue.V2
            .WriteSecretAsync(
                VaultSecretPath,
                new Dictionary<string, object> { ["SecretKey"] = request.NewSecretKey },
                mountPoint: "secret")
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Wrote new Stripe key to Vault for rotation {RotationId}", rotationId);

        // Step 3: Verify new key works against Stripe API
        var verified = await VerifyStripeKeyAsync(request.NewSecretKey, cancellationToken)
            .ConfigureAwait(false);

        if (!verified)
        {
            // Rollback: write old key back to Vault
            if (!string.IsNullOrEmpty(oldKey))
            {
                await _vaultClient.V1.Secrets.KeyValue.V2
                    .WriteSecretAsync(
                        VaultSecretPath,
                        new Dictionary<string, object> { ["SecretKey"] = oldKey },
                        mountPoint: "secret")
                    .ConfigureAwait(false);

                _logger.LogError(
                    "Stripe key verification failed for rotation {RotationId}. Rolled back to previous key.",
                    rotationId);
            }
            else
            {
                _logger.LogError(
                    "Stripe key verification failed for rotation {RotationId}. No previous key to rollback to.",
                    rotationId);
            }

            throw new InvalidOperationException(
                $"New Stripe key failed verification against Stripe API. Rotation {rotationId} rolled back.");
        }

        _logger.LogInformation(
            "Stripe key rotation {RotationId} verified; overlap expires at {OverlapExpiresAt:O}",
            rotationId, overlapExpiresAt);

        // Step 4: Publish the rotation started event (key material is NOT in the event)
        await _publishEndpoint.Publish(new StripeKeyRotationStartedEvent
        {
            RotationId = rotationId,
            OverlapExpiresAt = overlapExpiresAt
        }, cancellationToken).ConfigureAwait(false);

        // Step 5: Schedule RevokeOldStripeKeyJob after overlap window
        if (!string.IsNullOrEmpty(oldKey))
        {
            _backgroundJobClient.Schedule<RevokeOldStripeKeyJob>(
                job => job.ExecuteAsync(rotationId, oldKey, CancellationToken.None),
                TimeSpan.FromHours(overlapHours));

            _logger.LogInformation(
                "Scheduled old Stripe key revocation for rotation {RotationId} at {RevokeAt:O}",
                rotationId, overlapExpiresAt);
        }

        return new RotateStripeKeyResult
        {
            RotationId = rotationId,
            OverlapExpiresAt = overlapExpiresAt
        };
    }

    private async Task<bool> VerifyStripeKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            // StripeVerification HttpClient has BaseAddress = https://api.stripe.com/
            var client = _httpClientFactory.CreateClient("StripeVerification");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "v1/balance");
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe key verification HTTP call failed");
            return false;
        }
    }
}
