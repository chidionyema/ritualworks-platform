using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Infrastructure.Channels.Push;

/// <summary>
/// Push channel gateway. Iterates registered <see cref="IPushProvider"/>
/// implementations and stops at the first success or terminal failure.
/// Mirror of EmailChannelGateway pattern.
/// </summary>
public sealed class PushChannelGateway(
    IEnumerable<IPushProvider> providers,
    ILogger<PushChannelGateway> logger
) : IPushChannelGateway
{
    private static readonly ConcurrentDictionary<string, ResiliencePipeline> s_breakers = new();

    private readonly IReadOnlyList<IPushProvider> _providers = providers.ToList();

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_providers.Count == 0)
        {
            logger.LogError(
                "No IPushProvider implementations registered; cannot dispatch notification {NotificationId}",
                notification.Id);
            notification.MarkFailed("no-push-providers-registered");
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
                throw;
            }
            catch (Exception ex)
            {
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

            notification.RecordAttempt(new DeliveryAttempt(
                AttemptedAt: DateTime.UtcNow,
                ProviderName: provider.Name,
                ProviderMessageId: null,
                IsSuccess: false,
                ErrorMessage: result.Error));

            if (!result.IsRetryable)
            {
                logger.LogWarning(
                    "Provider {Provider} returned non-retryable failure for notification {NotificationId}: {Error}",
                    provider.Name, notification.Id, result.Error);
                notification.MarkFailed(result.Error ?? $"non-retryable failure from {provider.Name}");
                return;
            }

            logger.LogInformation(
                "Provider {Provider} retryable failure for notification {NotificationId}: {Error}; trying next",
                provider.Name, notification.Id, result.Error);
        }

        logger.LogError(
            "All {Count} providers exhausted for notification {NotificationId}",
            _providers.Count, notification.Id);
        notification.MarkFailed("all-providers-exhausted");
    }

    private static ResiliencePipeline BuildBreakerPipeline(string providerName)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = PushChannelResilienceConstants.CircuitBreakerFailureThreshold,
                SamplingDuration = TimeSpan.FromSeconds(PushChannelResilienceConstants.CircuitBreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(PushChannelResilienceConstants.CircuitBreakerBreakDurationSeconds),
                Name = $"push-{providerName}-cb",
            })
            .Build();
    }
}

internal static class PushChannelResilienceConstants
{
    public const int CircuitBreakerFailureThreshold = 5;
    public const double CircuitBreakerSamplingSeconds = 30;
    public const double CircuitBreakerBreakDurationSeconds = 30;
}
