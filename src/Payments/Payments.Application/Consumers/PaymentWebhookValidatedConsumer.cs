using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Application.Consumers;

/// <summary>
/// Consumes <see cref="PaymentWebhookValidatedEvent"/> published by the
/// webhook controller after signature verification. Dispatches by provider
/// + event type to either complete, fail, or flag the matching Payment
/// aggregate, then publishes the appropriate downstream domain event
/// (PaymentCompleted / PaymentSessionFailed / PaymentAmountMismatch /
/// PaymentVerified) via the per-context outbox.
///
/// Idempotency: the controller sets <c>MessageId == sha256(provider:eventId)[..16]</c>,
/// so MassTransit's inbox dedupes redeliveries without us needing to write
/// any extra dedupe logic — the second arrival is silently absorbed.
///
/// Per ADR-0009 this consumer touches no foreign-context state. It does
/// NOT update Order status — orders-svc consumes
/// <c>PaymentCompletedEvent</c> in Phase 4 to do that itself.
/// </summary>
// public (not internal) — MassTransit's AddConsumer<T, TDef>() takes T as
// a generic type argument from Infrastructure DI, which lives in a sibling
// assembly. Internal would be invisible there.
public sealed class PaymentWebhookValidatedConsumer(
    IEnumerable<IWebhookProcessor> processors,
    ILogger<PaymentWebhookValidatedConsumer> logger
) : IConsumer<PaymentWebhookValidatedEvent>
{
    public async Task Consume(ConsumeContext<PaymentWebhookValidatedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Processing webhook: provider={Provider}, eventType={EventType}, providerEventId={ProviderEventId}",
            evt.Provider, evt.EventType, evt.ProviderEventId);

        var providerEnum = ParseProvider(evt.Provider);
        if (providerEnum == null)
        {
            logger.LogError("Unknown webhook provider: {Provider}", evt.Provider);
            return;
        }

        var processor = processors.FirstOrDefault(p => p.Provider == providerEnum.Value);
        if (processor == null)
        {
            logger.LogWarning("No processor registered for provider: {Provider}", evt.Provider);
            return;
        }

        // 1. Re-validate and parse the payload
        var validationResult = await processor.ValidateAndParseAsync(
            evt.RawPayload, evt.Signature, context.CancellationToken);

        if (!validationResult.IsValid || validationResult.Event == null)
        {
            logger.LogWarning("Webhook validation failed for {Provider} {EventId}: {Message}",
                evt.Provider, evt.ProviderEventId, validationResult.ErrorMessage);
            return;
        }

        // 2. Process the event
        try
        {
            var result = await processor.ProcessEventAsync(validationResult.Event, context.CancellationToken);
            
            if (result.Processed)
            {
                logger.LogInformation("Webhook {EventId} processed successfully: {Message}",
                    evt.ProviderEventId, result.Message);
            }
            else
            {
                logger.LogInformation("Webhook {EventId} skipped or failed: {Message}",
                    evt.ProviderEventId, result.Message);
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            logger.LogInformation(
                "Webhook {ProviderEventId} concurrently processed by another worker; skipping",
                evt.ProviderEventId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process webhook {EventId}", evt.ProviderEventId);
            throw; // Re-throw to trigger MT retry
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        return inner?.GetType().Name == "PostgresException"
            && (inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string) == "23505";
    }

    private static PaymentProvider? ParseProvider(string provider) => provider switch
    {
        "Stripe" => PaymentProvider.Stripe,
        "PayPal" => PaymentProvider.PayPal,
        _ => null,
    };
}
