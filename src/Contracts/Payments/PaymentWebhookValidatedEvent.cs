namespace Haworks.Contracts.Payments;

/// <summary>
/// Published by the webhook controller after a provider webhook (Stripe,
/// PayPal, etc.) has been received and signature-validated. Hands the
/// validated payload off to a MassTransit consumer for processing.
///
/// Why this event exists:
///   The webhook HTTP request handler runs OUTSIDE any MT-managed
///   transaction. Doing business processing inline therefore requires
///   every publish to be paired with an explicit SaveChangesAsync, which
///   is fragile (Issue 2 caught two such drop-outbox-row bugs). By moving
///   processing into a MT consumer, the consume-side outbox filter
///   automatically wraps the work in a transaction — inbox dedupe +
///   downstream publishes + state writes commit atomically.
///
/// The event carries the RAW PAYLOAD plus the original signature headers
/// so the consumer can re-parse via the same provider processor (and, if
/// desired, re-validate the signature defensively). It does NOT carry the
/// already-parsed event object, because that object's <c>Data</c> field is
/// provider-specific (e.g. Stripe.Session) and not safely round-trippable
/// through MT's serializer.
///
/// MessageId on the published context should be set to the provider's
/// EventId, so MT's inbox dedupes idempotent webhook redeliveries (Stripe,
/// PayPal both retry on non-2xx; same EventId means same logical event).
/// </summary>
public sealed record PaymentWebhookValidatedEvent : DomainEvent
{
    /// <summary>The payment provider this webhook came from (e.g. "Stripe", "PayPal").</summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Provider's stable event ID (e.g. Stripe <c>evt_…</c>) — used as the
    /// MassTransit <c>MessageId</c> when publishing so the inbox dedupes
    /// idempotent webhook redeliveries. Distinct from the base
    /// <see cref="DomainEvent.EventId"/> which is a random trace GUID.
    /// </summary>
    public required string ProviderEventId { get; init; }

    /// <summary>Provider's event type (e.g. "payment_intent.succeeded").</summary>
    public required string EventType { get; init; }

    /// <summary>Raw webhook body as received over HTTP. Consumer re-parses this.</summary>
    public required string RawPayload { get; init; }

    /// <summary>
    /// Signature header value as received. JSON-encoded for PayPal (which
    /// has multiple signature headers). The consumer can re-run the
    /// processor's ValidateAndParseAsync defensively, though normally trust
    /// the controller's pre-validation.
    /// </summary>
    public required string Signature { get; init; }
}
