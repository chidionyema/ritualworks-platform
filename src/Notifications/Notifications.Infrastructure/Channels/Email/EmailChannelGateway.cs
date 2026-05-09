using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// L3 email channel gateway. Iterates registered <see cref="IEmailProvider"/>
/// implementations in DI registration order (SES -> SendGrid -> Mailgun, etc.)
/// and stops at the first <see cref="ProviderSendResult.IsSuccess"/> result
/// or first <see cref="ProviderSendResult.IsRetryable"/>=false (terminal).
///
/// Per-provider Polly circuit breaker: an in-memory <see cref="ResiliencePipeline"/>
/// is lazily constructed per provider name. After a configurable number of
/// consecutive failures (see <see cref="EmailChannelResilienceConstants"/>)
/// the breaker opens for the configured break duration; while open, calls to
/// that provider are skipped (the gateway falls through to the next provider
/// in the list rather than failing the notification outright).
///
/// Aggregate side-effects (per <see cref="Notification"/> state machine):
///   - On Success  -> <see cref="Notification.RecordAttempt(DeliveryAttempt)"/> (success) +
///                    <see cref="Notification.MarkSent(string)"/>.
///   - On NonRetryable -> <see cref="Notification.RecordAttempt(DeliveryAttempt)"/> (failure) +
///                    <see cref="Notification.MarkFailed(string)"/> (no further providers tried).
///   - On Retryable -> <see cref="Notification.RecordAttempt(DeliveryAttempt)"/> (failure) +
///                    continue to the next provider.
///   - All exhausted -> <see cref="Notification.MarkFailed(string)"/> with reason
///                    <c>all-providers-exhausted</c>.
/// </summary>
public sealed class EmailChannelGateway(
    IEnumerable<IEmailProvider> providers,
    ILogger<EmailChannelGateway> logger
) : IEmailChannelGateway
{
    /// <summary>Provider name -> ResiliencePipeline. Static so per-provider
    /// breaker state persists across the gateway's Scoped instances (one per
    /// consumer scope — gateway must be Scoped because providers like SES are
    /// registered Scoped). Concurrent because multiple consumer worker
    /// threads invoke the gateway in parallel.</summary>
    private static readonly ConcurrentDictionary<string, ResiliencePipeline> s_breakers = new();

    private readonly IReadOnlyList<IEmailProvider> _providers = providers.ToList();

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_providers.Count == 0)
        {
            logger.LogError(
                "No IEmailProvider implementations registered; cannot dispatch notification {NotificationId}",
                notification.Id);
            notification.MarkFailed("no-email-providers-registered");
            return;
        }

        foreach (var provider in _providers)
        {
            var pipeline = s_breakers.GetOrAdd(provider.Name, BuildBreakerPipeline);

            ProviderSendResult? result = null;
            try
            {
                result = await pipeline.ExecuteAsync(
                    async token => await provider.SendAsync(
                        notification.Recipient,
                        notification.Subject,
                        notification.Body,
                        token),
                    ct);
            }
            catch (BrokenCircuitException)
            {
                // Breaker is open for this provider — log + try the next one.
                logger.LogWarning(
                    "Provider {Provider} circuit OPEN for notification {NotificationId}; falling through",
                    provider.Name, notification.Id);
                notification.RecordAttempt(new DeliveryAttempt(
                    AttemptedAt: DateTime.UtcNow,
                    ProviderName: provider.Name,
                    ProviderMessageId: null,
                    IsSuccess: false,
                    ErrorMessage: "circuit-open"));
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Honour the caller's cancellation immediately; don't try further providers.
                throw;
            }
            catch (Exception ex)
            {
                // Treat exceptions as retryable per provider — log + try next.
                // Polly's CB inside the pipeline will count this toward the
                // failure threshold, so a flapping provider naturally gets
                // skipped on subsequent calls.
                logger.LogWarning(ex,
                    "Provider {Provider} threw for notification {NotificationId}; treating as retryable",
                    provider.Name, notification.Id);
                notification.RecordAttempt(new DeliveryAttempt(
                    AttemptedAt: DateTime.UtcNow,
                    ProviderName: provider.Name,
                    ProviderMessageId: null,
                    IsSuccess: false,
                    ErrorMessage: ex.Message));
                continue;
            }

            if (result.IsSuccess)
            {
                var providerMessageId = result.ProviderMessageId ?? Guid.NewGuid().ToString("N");
                notification.RecordAttempt(new DeliveryAttempt(
                    AttemptedAt: DateTime.UtcNow,
                    ProviderName: provider.Name,
                    ProviderMessageId: providerMessageId,
                    IsSuccess: true,
                    ErrorMessage: null));
                notification.MarkSent(providerMessageId);
                logger.LogInformation(
                    "Notification {NotificationId} sent via {Provider} (messageId={ProviderMessageId})",
                    notification.Id, provider.Name, providerMessageId);
                return;
            }

            // Failure — record + branch on retryability.
            notification.RecordAttempt(new DeliveryAttempt(
                AttemptedAt: DateTime.UtcNow,
                ProviderName: provider.Name,
                ProviderMessageId: null,
                IsSuccess: false,
                ErrorMessage: result.Error));

            if (!result.IsRetryable)
            {
                // Hard failure (e.g., invalid recipient, suppressed) — do not
                // try secondary providers; the same input would fail there too.
                logger.LogWarning(
                    "Provider {Provider} returned non-retryable failure for notification {NotificationId}: {Error}",
                    provider.Name, notification.Id, result.Error);
                notification.MarkFailed(result.Error ?? $"non-retryable failure from {provider.Name}");
                return;
            }

            // Retryable — fall through to the next provider in the list.
            logger.LogInformation(
                "Provider {Provider} retryable failure for notification {NotificationId}: {Error}; trying next",
                provider.Name, notification.Id, result.Error);
        }

        // All providers attempted, none succeeded and none asked us to stop.
        logger.LogError(
            "All {Count} providers exhausted for notification {NotificationId}",
            _providers.Count, notification.Id);
        notification.MarkFailed("all-providers-exhausted");
    }

    /// <summary>Per-provider Polly v8 circuit breaker pipeline. Keep simple —
    /// failure-ratio threshold + fixed break duration. Tunable via a future
    /// EmailChannelResilienceOptions binding when the surface stabilises.</summary>
    private static ResiliencePipeline BuildBreakerPipeline(string providerName)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = EmailChannelResilienceConstants.CircuitBreakerFailureThreshold,
                SamplingDuration = TimeSpan.FromSeconds(EmailChannelResilienceConstants.CircuitBreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(EmailChannelResilienceConstants.CircuitBreakerBreakDurationSeconds),
                Name = $"email-{providerName}-cb",
            })
            .Build();
    }
}

/// <summary>
/// Centralised tunables for the email-gateway breaker. Lives next to the
/// gateway because it's gateway-specific (a future Infrastructure/Constants
/// reorg may relocate alongside the broader ResilienceConstants).
/// </summary>
internal static class EmailChannelResilienceConstants
{
    /// <summary>Consecutive provider failures before the breaker opens.</summary>
    public const int CircuitBreakerFailureThreshold = 5;

    /// <summary>Polly v8 sampling window over which the failure ratio is computed.</summary>
    public const double CircuitBreakerSamplingSeconds = 30;

    /// <summary>How long the breaker stays open before half-open probing.</summary>
    public const double CircuitBreakerBreakDurationSeconds = 30;
}
