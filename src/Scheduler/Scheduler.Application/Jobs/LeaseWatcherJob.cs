using Haworks.Contracts.Rotation;
using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Domain.Entities;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using VaultSharp;
using VaultSharp.Core;

namespace Haworks.Scheduler.Application.Jobs;

/// <summary>
/// Hangfire recurring job (hourly) that monitors VaultLease rows and rotates
/// credentials approaching expiry (80% of TTL elapsed).
/// </summary>
public sealed class LeaseWatcherJob
{
    private const int BatchSize = 50;
    private const int StaleRotatingMinutes = 10;

    private readonly ILeaseRepository _leaseRepo;
    private readonly IVaultClient _vaultClient;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<LeaseWatcherJob> _logger;

    public LeaseWatcherJob(
        ILeaseRepository leaseRepo,
        IVaultClient vaultClient,
        IPublishEndpoint publishEndpoint,
        ILogger<LeaseWatcherJob> logger)
    {
        _leaseRepo = leaseRepo;
        _vaultClient = vaultClient;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("LeaseWatcherJob starting cycle");

        // Reset stale Rotating rows (stuck > 10 min)
        await ResetStaleRotatingLeasesAsync(ct).ConfigureAwait(false);

        // Find leases needing rotation
        var needsRotation = await _leaseRepo
            .GetActiveLeasesNeedingRotationAsync(BatchSize, ct)
            .ConfigureAwait(false);

        if (needsRotation.Count == 0)
        {
            _logger.LogDebug("LeaseWatcherJob: no leases need rotation");
            return;
        }

        _logger.LogInformation("LeaseWatcherJob: {Count} leases need rotation", needsRotation.Count);

        foreach (var lease in needsRotation)
        {
            if (ct.IsCancellationRequested) break;
            await RotateLeaseAsync(lease, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("LeaseWatcherJob cycle complete");
    }

    private async Task RotateLeaseAsync(VaultLease lease, CancellationToken ct)
    {
        try
        {
            lease.MarkRotating();
            await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (string.Equals(ex.GetType().Name, "DbUpdateConcurrencyException", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Concurrency conflict marking lease {LeaseId} as Rotating; another instance handled it",
                lease.Id);
            return;
        }

        try
        {
            switch (lease.CredentialType)
            {
                case "database":
                    await RotateDatabaseCredentialAsync(lease, ct).ConfigureAwait(false);
                    break;
                case "pki":
                    await RotatePkiCertificateAsync(lease, ct).ConfigureAwait(false);
                    break;
                case "kv":
                    // KV rotation is handled by specific rotation jobs (RotateJwtKeyJob, StripeKeyRotationJob)
                    // Mark as rotated with a placeholder to reset the TTL timer
                    lease.MarkRotated($"kv-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddHours(720));
                    await _leaseRepo.AddAuditEntryAsync(
                        RotationAuditEntry.Record(lease.Id, "rotate", success: true), ct).ConfigureAwait(false);
                    await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    _logger.LogWarning("Unknown credential type {Type} for lease {LeaseId}", lease.CredentialType, lease.Id);
                    lease.MarkFailed($"Unknown credential type: {lease.CredentialType}");
                    await _leaseRepo.AddAuditEntryAsync(
                        RotationAuditEntry.Record(lease.Id, "fail", success: false, error: lease.CredentialType), ct).ConfigureAwait(false);
                    await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (VaultApiException ex)
        {
            _logger.LogError(ex, "Vault API error rotating lease {LeaseId} for {Service}/{Role}",
                lease.Id, lease.ServiceName, lease.RoleName);
            await HandleRotationFailureAsync(lease, ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error rotating lease {LeaseId} for {Service}/{Role}",
                lease.Id, lease.ServiceName, lease.RoleName);
            await HandleRotationFailureAsync(lease, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleRotationFailureAsync(VaultLease lease, string reason, CancellationToken ct)
    {
        lease.MarkFailed(reason);
        await _leaseRepo.AddAuditEntryAsync(
            RotationAuditEntry.Record(lease.Id, "fail", success: false, error: reason), ct).ConfigureAwait(false);

        // Publish before SaveChanges — MassTransit EF Outbox writes the
        // message row in the same transaction as the failure update.
        await _publishEndpoint.Publish(new RotationFailedEvent
        {
            ServiceName = lease.ServiceName,
            RoleName = lease.RoleName,
            Reason = reason,
            AttemptCount = 1
        }, ct).ConfigureAwait(false);

        await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task RotateDatabaseCredentialAsync(VaultLease lease, CancellationToken ct)
    {
        var secret = await _vaultClient.V1.Secrets.Database
            .GetCredentialsAsync(lease.RoleName)
            .ConfigureAwait(false);

        var newLeaseId = secret.LeaseId;
        var leaseDuration = TimeSpan.FromSeconds(secret.LeaseDurationSeconds);
        var expiresAt = DateTimeOffset.UtcNow + leaseDuration;

        lease.MarkRotated(newLeaseId, expiresAt);
        await _leaseRepo.AddAuditEntryAsync(
            RotationAuditEntry.Record(lease.Id, "rotate", success: true, newLeaseId: newLeaseId), ct).ConfigureAwait(false);

        // Publish before SaveChanges — MassTransit EF Outbox writes the
        // message row in the same transaction as the lease update.
        await _publishEndpoint.Publish(new CredentialRotatedEvent
        {
            ServiceName = lease.ServiceName,
            RoleName = lease.RoleName,
            LeaseId = newLeaseId,
            ExpiresAt = expiresAt
        }, ct).ConfigureAwait(false);

        await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Database credential rotated for {Service}/{Role}, new lease expires at {ExpiresAt}",
            lease.ServiceName, lease.RoleName, expiresAt);
    }

    private async Task RotatePkiCertificateAsync(VaultLease lease, CancellationToken ct)
    {
        var certResult = await _vaultClient.V1.Secrets.PKI
            .GetCredentialsAsync(
                lease.RoleName,
                new VaultSharp.V1.SecretsEngines.PKI.CertificateCredentialsRequestOptions
                {
                    CommonName = $"{lease.ServiceName}.internal",
                    TimeToLive = "720h"
                },
                pkiBackendMountPoint: "pki_int")
            .ConfigureAwait(false);

        var serialNumber = certResult.Data.SerialNumber;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(720);
        var newLeaseId = $"pki-{serialNumber}";

        lease.MarkRotated(newLeaseId, expiresAt);
        await _leaseRepo.AddAuditEntryAsync(
            RotationAuditEntry.Record(lease.Id, "rotate", success: true, newLeaseId: newLeaseId), ct).ConfigureAwait(false);

        // Publish before SaveChanges — MassTransit EF Outbox writes the
        // message row in the same transaction as the lease update.
        await _publishEndpoint.Publish(new CertificateRotatedEvent
        {
            ServiceName = lease.ServiceName,
            CommonName = $"{lease.ServiceName}.internal",
            ExpiresAt = expiresAt,
            SerialNumber = serialNumber
        }, ct).ConfigureAwait(false);

        await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "PKI certificate rotated for {Service}/{Role}, serial {Serial}",
            lease.ServiceName, lease.RoleName, serialNumber);
    }

    private async Task ResetStaleRotatingLeasesAsync(CancellationToken ct)
    {
        var staleLeases = await _leaseRepo
            .GetStaleRotatingLeasesAsync(StaleRotatingMinutes, ct)
            .ConfigureAwait(false);

        foreach (var lease in staleLeases)
        {
            _logger.LogWarning(
                "Resetting stale Rotating lease {LeaseId} for {Service}/{Role} to Failed",
                lease.Id, lease.ServiceName, lease.RoleName);

            lease.ResetStaleRotating("Stale rotation detected -- exceeded 10 minute threshold");
            await _leaseRepo.AddAuditEntryAsync(
                RotationAuditEntry.Record(lease.Id, "fail", success: false, error: "Stale rotation reset"), ct).ConfigureAwait(false);
        }

        if (staleLeases.Count > 0)
        {
            await _leaseRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
