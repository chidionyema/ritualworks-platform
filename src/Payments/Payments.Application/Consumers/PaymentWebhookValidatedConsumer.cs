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
    IPaymentRepository payments,
    IDomainEventPublisher eventPublisher,
    ILogger<PaymentWebhookValidatedConsumer> logger
) : IConsumer<PaymentWebhookValidatedEvent>
{
    private static readonly JsonDocumentOptions s_jsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public async Task Consume(ConsumeContext<PaymentWebhookValidatedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation(
            "Processing webhook: provider={Provider}, eventType={EventType}, providerEventId={ProviderEventId}",
            evt.Provider, evt.EventType, evt.ProviderEventId);

        // Application-level idempotency. The cheap read-side check skips
        // re-work when a redelivery arrives after the first consume committed.
        // The WebhookEvent row itself is written by the dispatch branches
        // (in the same SaveChanges as the Payment state mutation), so the
        // unique index on (Provider, ProviderEventId) gives us atomic
        // "state change + dedup row" semantics in production.
        //
        // This is best-effort — a tight burst of redeliveries can race the
        // SELECT and all see "no row". Production resolves the race via the
        // EF outbox: publishes are captured in the same TX as the WebhookEvent
        // insert, and the second-place SaveChanges fails on the unique index
        // and rolls back the outbox row too, so RabbitMQ sees one publish.
        // The in-memory test transport publishes immediately and cannot
        // replicate that, hence the WebhookEvent row count (not the publish
        // count) is the production-correct idempotency assertion.
        var providerEnum = ParseProvider(evt.Provider);
        if (providerEnum.HasValue && !string.IsNullOrEmpty(evt.ProviderEventId))
        {
            var alreadyProcessed = await payments.WebhookEventExistsAsync(
                providerEnum.Value, evt.ProviderEventId, context.CancellationToken);
            if (alreadyProcessed)
            {
                logger.LogInformation(
                    "Webhook {ProviderEventId} already processed (provider={Provider}); skipping",
                    evt.ProviderEventId, evt.Provider);
                return;
            }

            // Stage the WebhookEvent in the DbContext so it commits in the
            // same SaveChanges as the Payment state mutation done by the
            // dispatch branch. Production: outbox row + state row + dedup
            // row all atomic. The unique index on (Provider, ProviderEventId)
            // turns concurrent in-flight consumes into a deterministic
            // single-winner race at SaveChanges time.
            var webhookEvent = WebhookEvent.Create(
                providerEnum.Value, evt.ProviderEventId, evt.EventType, evt.RawPayload);
            await payments.AddWebhookEventAsync(webhookEvent, context.CancellationToken);
        }

        // Provider dispatch. Each branch is responsible for parsing the
        // provider's payload, looking up the Payment aggregate, transitioning
        // it, and publishing the right downstream event.
        try
        {
            switch (evt.Provider)
            {
                case "Stripe":
                    await HandleStripeAsync(context, evt);
                    break;
                case "PayPal":
                    await HandlePayPalAsync(context, evt);
                    break;
                default:
                    logger.LogError("Unknown webhook provider: {Provider}", evt.Provider);
                    return;
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race lost: another consumer beat us to inserting the
            // WebhookEvent row. Their SaveChanges already wrote the
            // Payment update + outbox publish; ours rolled back. Treat
            // this as a successful idempotent skip rather than an error.
            logger.LogInformation(
                "Webhook {ProviderEventId} concurrently processed by another worker; skipping",
                evt.ProviderEventId);
        }
    }

    /// <summary>
    /// Postgres reports unique-constraint failures with SQLSTATE 23505.
    /// Npgsql wraps that as PostgresException inside DbUpdateException.
    /// We can't reference Npgsql here without a layer leak, so match by
    /// SqlState string on the inner exception via reflection-free duck typing.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        return inner?.GetType().Name == "PostgresException"
            && (inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string) == "23505";
    }

    private async Task HandleStripeAsync(ConsumeContext<PaymentWebhookValidatedEvent> context, PaymentWebhookValidatedEvent evt)
    {
        // Stripe events of interest: payment_intent.succeeded, checkout.session.completed,
        // payment_intent.payment_failed, charge.refunded. Only the first two are
        // wired here; the consumer no-ops for the others (Phase 3 scope).
        var (sessionId, transactionId, paidAmount, paymentMethod) = ParseStripePayload(evt.RawPayload, evt.EventType);

        switch (evt.EventType)
        {
            case "checkout.session.completed":
            case "payment_intent.succeeded":
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    logger.LogWarning("Stripe {EventType} missing session/intent id", evt.EventType);
                    return;
                }

                var payment = await payments.GetByProviderSessionTrackedAsync(
                    PaymentProvider.Stripe, sessionId, context.CancellationToken);

                if (payment is null)
                {
                    logger.LogWarning(
                        "Payment not found for Stripe session {SessionId}; ack-and-noop", sessionId);
                    // Ack the message rather than throw. Throwing kicks MT
                    // into retry-then-DLQ, but a missing payment row is a
                    // legitimate state in our flow — webhook may arrive
                    // before the checkout API call lands. Phase 4's saga
                    // wiring re-creates the payment from PaymentSessionRequested.
                    return;
                }

                // Idempotency at the application level: if already complete,
                // skip the publish so we don't emit duplicate
                // PaymentCompletedEvent rows. (MT inbox handles webhook
                // replays at the transport level; this catches the case
                // where the same logical webhook is delivered via two
                // different ProviderEventIds — rare but possible.)
                if (payment.IsComplete)
                {
                    logger.LogInformation("Payment {PaymentId} already completed; skipping", payment.Id);
                    return;
                }

                // Amount validation: if the gateway captured a different
                // amount than we authorized, transition to Flagged + publish
                // PaymentAmountMismatchEvent for ops review. Only proceed
                // with completion when amounts match.
                if (paidAmount.HasValue && paidAmount.Value != payment.Amount)
                {
                    payment.Flag();

                    // Publish BEFORE SaveChangesAsync (matches the success
                    // branch + production outbox semantics: the publish is
                    // captured into the OutboxMessage row written in the same
                    // EF transaction, so state change + downstream event
                    // commit atomically. Save-then-publish would double the
                    // failure surface — if save threw after retries, the
                    // publish would never run and ops would never see the
                    // mismatch alert.)
                    await eventPublisher.PublishAsync(new PaymentAmountMismatchEvent
                    {
                        PaymentId = payment.Id,
                        OrderId = payment.OrderId,
                        Provider = "Stripe",
                        ActualPaid = paidAmount.Value,
                        ExpectedTotal = payment.Amount,
                        Difference = Math.Abs(paidAmount.Value - payment.Amount),
                        Reason = $"Stripe captured {paidAmount.Value} {payment.Currency}; expected {payment.Amount} {payment.Currency}",
                    }, context.CancellationToken);

                    await payments.SaveChangesAsync(context.CancellationToken);

                    logger.LogWarning(
                        "Payment {PaymentId} flagged: expected={Expected}, actual={Actual}",
                        payment.Id, payment.Amount, paidAmount.Value);
                    return;
                }

                payment.MarkCompleted(transactionId ?? sessionId, paymentMethod ?? "card");

                // Publish BEFORE SaveChanges. The EF outbox writes the
                // OutboxMessage row in the same transaction as the state
                // change; both commit atomically.
                await eventPublisher.PublishAsync(new PaymentCompletedEvent
                {
                    PaymentId = payment.Id,
                    OrderId = payment.OrderId,
                    SagaId = payment.SagaId,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    Provider = "Stripe",
                    TransactionReference = payment.ProviderTransactionId,
                }, context.CancellationToken);

                await payments.SaveChangesAsync(context.CancellationToken);
                logger.LogInformation("Payment {PaymentId} marked Completed", payment.Id);
                break;
            }

            case "payment_intent.payment_failed":
            case "checkout.session.expired":
            {
                if (string.IsNullOrEmpty(sessionId)) return;

                var payment = await payments.GetByProviderSessionTrackedAsync(
                    PaymentProvider.Stripe, sessionId, context.CancellationToken);
                if (payment is null) return;
                if (payment.Status == PaymentStatus.Failed) return;

                payment.MarkFailed();
                await eventPublisher.PublishAsync(new PaymentSessionFailedEvent
                {
                    OrderId = payment.OrderId,
                    SagaId = payment.SagaId,
                    Provider = "Stripe",
                    ErrorCode = evt.EventType,
                    ErrorMessage = $"Stripe reported {evt.EventType} for session {sessionId}",
                    AttemptNumber = 1,
                    IsFinalAttempt = true,
                }, context.CancellationToken);
                await payments.SaveChangesAsync(context.CancellationToken);
                logger.LogInformation("Payment {PaymentId} marked Failed (reason={Reason})", payment.Id, evt.EventType);
                break;
            }

            default:
                logger.LogDebug("Stripe event {EventType} ignored", evt.EventType);
                break;
        }
    }

    private async Task HandlePayPalAsync(ConsumeContext<PaymentWebhookValidatedEvent> context, PaymentWebhookValidatedEvent evt)
    {
        // Phase 3 ships only the dispatch skeleton for PayPal — the real
        // verify-signature-via-PayPal-API call + provider payload parsing
        // is left for a follow-up. For now we log + no-op so PayPal
        // webhooks don't crash the consumer pipeline.
        logger.LogInformation(
            "PayPal webhook received but processing not yet implemented (eventType={EventType})", evt.EventType);
        await Task.CompletedTask;
    }

    private static (string? sessionId, string? transactionId, decimal? paidAmount, string? paymentMethod)
        ParseStripePayload(string rawPayload, string eventType)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawPayload, s_jsonOptions);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return (null, null, null, null);
            if (!data.TryGetProperty("object", out var obj)) return (null, null, null, null);

            string? sessionId = null;
            string? transactionId = null;
            decimal? paidAmount = null;
            string? paymentMethod = null;

            // For checkout.session.completed: object.id IS the session id;
            // payment_intent / amount_total / payment_method_types[0].
            // For payment_intent.succeeded: object.id IS the payment intent id;
            // amount / payment_method_types[0]. We treat the intent id as the
            // session id for lookup purposes (catalog payment row may be
            // keyed off either depending on flow).
            if (obj.TryGetProperty("id", out var idEl)) sessionId = idEl.GetString();

            if (eventType == "checkout.session.completed" && obj.TryGetProperty("payment_intent", out var intentEl))
            {
                transactionId = intentEl.GetString();
            }
            else if (eventType == "payment_intent.succeeded")
            {
                transactionId = sessionId;
            }

            if (obj.TryGetProperty("amount_total", out var atEl) && atEl.ValueKind == JsonValueKind.Number)
            {
                paidAmount = atEl.GetInt64() / 100m; // Stripe uses minor units
            }
            else if (obj.TryGetProperty("amount", out var amEl) && amEl.ValueKind == JsonValueKind.Number)
            {
                paidAmount = amEl.GetInt64() / 100m;
            }

            if (obj.TryGetProperty("payment_method_types", out var pmEl) && pmEl.ValueKind == JsonValueKind.Array && pmEl.GetArrayLength() > 0)
            {
                paymentMethod = pmEl[0].GetString();
            }

            return (sessionId, transactionId, paidAmount, paymentMethod);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// PaymentWebhookValidatedEvent.Provider is a string ("Stripe", "PayPal").
    /// WebhookEventExistsAsync takes the PaymentProvider enum. Map carefully —
    /// unknown providers return null so the idempotency check is skipped (still
    /// safe; the dispatch switch will hit the default branch + log an error).
    /// </summary>
    private static PaymentProvider? ParseProvider(string provider) => provider switch
    {
        "Stripe" => PaymentProvider.Stripe,
        "PayPal" => PaymentProvider.PayPal,
        _ => null,
    };
}
