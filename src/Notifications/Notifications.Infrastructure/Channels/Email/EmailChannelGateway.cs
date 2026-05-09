using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Resilience.Exceptions;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Consumers;
using Haworks.Notifications.Application.Templates;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// L3 — Email channel gateway. Implements both:
///   * <see cref="IEmailChannelGateway"/> (the L0 contract — fire-and-forget Task).
///   * <see cref="IChannelDispatcher"/> (the L3 contract — returns
///     <see cref="DeliveryOutcome"/> so the dispatch consumer can drive
///     the Notification state machine).
///
/// Iterates registered <see cref="IEmailProvider"/>s in DI registration
/// order (priority is established at registration time). Each provider
/// call is wrapped in a per-provider Polly policy (combined retry +
/// circuit breaker, lifted from
/// <see cref="ResilienceOptions.ForExternalApi"/>). Per-provider state
/// is critical: an unhealthy SES circuit must NOT poison the SendGrid
/// path, so each provider gets its own breaker keyed by
/// <see cref="IEmailProvider.Name"/>.
///
/// Decision tree per provider:
///   * <c>IsSuccess</c> → <see cref="DeliveryOutcome.Sent"/>. Stop.
///   * <c>IsRetryable=false</c> → <see cref="DeliveryOutcome.Failed"/>.
///     Stop. (Invalid recipient / domain rejection / account suspension
///     produces the same answer at every provider — short-circuit.)
///   * <c>IsRetryable=true</c> → record + advance to next provider.
///   * Open circuit (<see cref="BrokenCircuitException"/> or
///     <see cref="CircuitBreakerOpenException"/>) → record + advance.
///   * Any other thrown exception → record + advance (defensive — providers
///     are supposed to translate exceptions into <see cref="ProviderSendResult"/>).
///
/// Per-provider attempts are appended to
/// <see cref="Notification.DeliveryAttempts"/> via
/// Notification.RecordAttempt(...) when the L1.A domain method is
/// implemented; until then attempts are still logged at the gateway.
/// </summary>
public sealed class EmailChannelGateway : IEmailChannelGateway, IChannelDispatcher
{
    // Per-provider policy cache. Built on first use, keyed by provider
    // name. ConcurrentDictionary so multiple consumers running in
    // parallel share the same circuit-breaker state per provider — a
    // 500 from SES surfaces in every consumer's view of the circuit,
    // which is the whole point of having a circuit breaker.
    private readonly ConcurrentDictionary<string, IAsyncPolicy<ProviderSendResult>> _policies = new();

    private readonly IReadOnlyList<IEmailProvider> _providers;
    private readonly IResiliencePolicyFactory _policyFactory;
    private readonly ILogger<EmailChannelGateway> _logger;

    public EmailChannelGateway(
        IEnumerable<IEmailProvider> providers,
        IResiliencePolicyFactory policyFactory,
        ILogger<EmailChannelGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _policyFactory = policyFactory ?? throw new ArgumentNullException(nameof(policyFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _providers = providers.ToList();
    }

    /// <summary>
    /// L0-compatible entrypoint. Drives the dispatch using whatever
    /// rendering is already on the aggregate (Subject / Body) — callers
    /// going through this overload do not yet have a
    /// <see cref="RenderedNotification"/>. The consumer pipeline always
    /// uses <see cref="DispatchAsync"/> instead.
    /// </summary>
    public Task SendAsync(Notification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var rendered = new RenderedNotification(
            notification.Subject ?? string.Empty,
            notification.Body ?? string.Empty,
            null);

        return DispatchAsync(notification, rendered, ct);
    }

    public async Task<DeliveryOutcome> DispatchAsync(
        Notification notification,
        RenderedNotification rendered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(rendered);

        if (_providers.Count == 0)
        {
            _logger.LogError(
                "No IEmailProvider registered; cannot dispatch notification {NotificationId}",
                notification.Id);
            RecordAttemptSafe(notification, "no-provider", null, success: false, "no-provider-registered");
            return DeliveryOutcome.Exhausted("no-provider-registered");
        }

        var recipient = notification.Recipient;
        var subject = rendered.Subject;
        var body = rendered.Body;
        string? lastError = null;

        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            var policy = _policies.GetOrAdd(provider.Name, BuildPolicy);

            ProviderSendResult result;
            try
            {
                result = await policy.ExecuteAsync(
                    (_, token) => provider.SendAsync(recipient, subject, body, token),
                    new Context($"email:{provider.Name}"),
                    ct).ConfigureAwait(false);
            }
            catch (BrokenCircuitException ex)
            {
                _logger.LogWarning(ex,
                    "Email provider {Provider} circuit is open; advancing for notification {NotificationId}",
                    provider.Name, notification.Id);
                lastError = $"{provider.Name}-circuit-open";
                RecordAttemptSafe(notification, provider.Name, null, success: false, lastError);
                continue;
            }
            catch (CircuitBreakerOpenException ex)
            {
                _logger.LogWarning(ex,
                    "Email provider {Provider} circuit is open; advancing for notification {NotificationId}",
                    provider.Name, notification.Id);
                lastError = $"{provider.Name}-circuit-open";
                RecordAttemptSafe(notification, provider.Name, null, success: false, lastError);
                continue;
            }
            catch (OperationCanceledException)
            {
                // Cooperative cancellation — surface to the consumer so
                // MassTransit's retry pipeline can take over.
                throw;
            }
            catch (Exception ex)
            {
                // Defensive: providers should translate exceptions into
                // ProviderSendResult; leak protection so a single
                // misbehaving provider doesn't sink the whole chain.
                _logger.LogWarning(ex,
                    "Email provider {Provider} threw unhandled exception; advancing for notification {NotificationId}",
                    provider.Name, notification.Id);
                lastError = $"{provider.Name}-exception";
                RecordAttemptSafe(notification, provider.Name, null, success: false, lastError);
                continue;
            }

            if (result.IsSuccess)
            {
                RecordAttemptSafe(notification, provider.Name, result.ProviderMessageId, success: true, errorMessage: null);
                return DeliveryOutcome.Sent(provider.Name, result.ProviderMessageId);
            }

            // Failure path: distinguish retryable (advance) from
            // non-retryable (short-circuit — invalid recipient is the
            // same answer at every provider).
            RecordAttemptSafe(notification, provider.Name, null, success: false, result.Error);
            lastError = result.Error;

            if (!result.IsRetryable)
            {
                _logger.LogInformation(
                    "Email provider {Provider} returned non-retryable failure for notification {NotificationId}; aborting chain",
                    provider.Name, notification.Id);
                return DeliveryOutcome.Failed(provider.Name, result.Error);
            }

            _logger.LogInformation(
                "Email provider {Provider} returned retryable failure for notification {NotificationId}; advancing to next provider",
                provider.Name, notification.Id);
        }

        return DeliveryOutcome.Exhausted(lastError);
    }

    private IAsyncPolicy<ProviderSendResult> BuildPolicy(string providerName)
    {
        // Combined policy keyed per-provider so circuit-breaker state is
        // isolated. shouldRetryResult also flips the breaker on
        // ProviderSendResult.IsRetryable=true so a provider returning
        // Retryable consistently is treated the same as one throwing.
        var options = ResilienceOptions.ForExternalApi(providerName, includeBulkhead: false);
        return _policyFactory.CreateCombinedPolicy<ProviderSendResult>(
            options,
            shouldRetryResult: r => r is { IsSuccess: false, IsRetryable: true });
    }

    private void RecordAttemptSafe(
        Notification notification,
        string providerName,
        string? providerMessageId,
        bool success,
        string? errorMessage)
    {
        var attempt = new DeliveryAttempt(
            DateTime.UtcNow,
            providerName,
            providerMessageId,
            success,
            errorMessage);

        try
        {
            notification.RecordAttempt(attempt);
        }
        catch (NotImplementedException)
        {
            // TODO(notif-L3): wait for L1.A — until Notification.RecordAttempt
            // is implemented, audit-trail rows are only visible via the
            // gateway log. The dispatch consumer's own logging retains
            // operator-level visibility.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record DeliveryAttempt for notification {NotificationId} via {Provider}",
                notification.Id, providerName);
        }
    }
}
