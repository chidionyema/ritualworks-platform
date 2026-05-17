using System.Security.Cryptography;
using Haworks.Contracts.Secrets;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using VaultSharp;

namespace Haworks.Scheduler.Application.Jobs;

/// <summary>
/// Monthly Hangfire recurring job that rotates the JWT signing key.
/// 1. Reads current key from Vault KV v2
/// 2. Copies it to the jwt-previous path (overlap window)
/// 3. Generates a new RSA-2048 key
/// 4. Writes new key to Vault
/// 5. Publishes JwtKeyRotatedEvent
/// 6. Schedules ClearPreviousJwtKeyJob in 15 minutes
/// </summary>
public sealed class RotateJwtKeyJob
{
    private const string VaultMountPoint = "secret";
    private const string JwtKeyPath = "identity/jwt";
    private const string JwtPreviousKeyPath = "identity/jwt-previous";
    private const string KeyField = "signing_key";

    private readonly IVaultClient _vaultClient;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<RotateJwtKeyJob> _logger;

    public RotateJwtKeyJob(
        IVaultClient vaultClient,
        IPublishEndpoint publishEndpoint,
        IBackgroundJobClient backgroundJobClient,
        ILogger<RotateJwtKeyJob> logger)
    {
        _vaultClient = vaultClient;
        _publishEndpoint = publishEndpoint;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken ct)
    {
        var rotationId = Guid.NewGuid();
        _logger.LogInformation("Starting JWT key rotation {RotationId}", rotationId);

        // Step 1: Read current key
        var currentSecret = await _vaultClient.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(JwtKeyPath, mountPoint: VaultMountPoint)
            .ConfigureAwait(false);

        var currentKey = currentSecret.Data.Data.TryGetValue(KeyField, out var keyValue)
            ? keyValue?.ToString() ?? string.Empty
            : string.Empty;

        // Step 2: Copy current to jwt-previous for overlap window
        if (!string.IsNullOrEmpty(currentKey))
        {
            await _vaultClient.V1.Secrets.KeyValue.V2
                .WriteSecretAsync(
                    JwtPreviousKeyPath,
                    new Dictionary<string, object> { [KeyField] = currentKey },
                    mountPoint: VaultMountPoint)
                .ConfigureAwait(false);

            _logger.LogInformation("Copied current JWT key to previous path for overlap window");
        }

        // Step 3: Generate new RSA-2048 key
        using var rsa = RSA.Create(2048);
        var newKeyPem = rsa.ExportRSAPrivateKeyPem();

        // Step 4: Write new key to Vault
        await _vaultClient.V1.Secrets.KeyValue.V2
            .WriteSecretAsync(
                JwtKeyPath,
                new Dictionary<string, object> { [KeyField] = newKeyPem },
                mountPoint: VaultMountPoint)
            .ConfigureAwait(false);

        _logger.LogInformation("New JWT signing key written to Vault for rotation {RotationId}", rotationId);

        // Step 5: Publish rotation event
        await _publishEndpoint.Publish(new JwtKeyRotatedEvent
        {
            RotationId = rotationId,
            RotatedAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);

        // Step 6: Schedule cleanup of previous key after overlap window (15 min)
        _backgroundJobClient.Schedule<ClearPreviousJwtKeyJob>(
            job => job.RunAsync(CancellationToken.None),
            TimeSpan.FromMinutes(15));

        _logger.LogInformation(
            "JWT key rotation {RotationId} complete; ClearPreviousJwtKeyJob scheduled in 15 minutes",
            rotationId);
    }
}
