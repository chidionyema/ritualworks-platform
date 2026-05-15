using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Haworks.Contracts.Payments;
using Haworks.Payments.Api.Webhooks;

namespace Haworks.Payments.Api.Controllers;

/// <summary>
/// Provider webhook ingress. The job here is intentionally narrow:
///   1. Read the raw body (signature is computed over the bytes as received).
///   2. Validate the provider signature inline.
///   3. Publish <see cref="PaymentWebhookValidatedEvent"/> via the per-context
///      outbox, with <c>MessageId == provider EventId</c> for inbox dedupe.
///   4. Return 200 to the provider.
///
/// Business processing happens asynchronously in
/// <c>PaymentWebhookValidatedConsumer</c> (Phase 3c) — this keeps the
/// HTTP path fast (provider retry timeout is short) and gives us
/// transactional guarantees on the consume side via the EF outbox filter.
///
/// Per ADR-0009 this controller injects no IOrderRepository / no
/// foreign-context types. It only writes the validated event.
/// </summary>
// Webhook endpoints are called by payment providers — signature validation replaces auth
[AllowAnonymous]
[ApiController]
[Route("webhooks")]
public sealed class WebhooksController(
    IPublishEndpoint publishEndpoint,
    IOptions<WebhookOptions> options,
    ILogger<WebhooksController> logger) : ControllerBase
{
    [HttpPost("stripe")]
    public async Task<IActionResult> Stripe(CancellationToken ct)
    {
        Request.EnableBuffering();
        var signature = Request.Headers["Stripe-Signature"].ToString();
        if (string.IsNullOrEmpty(signature))
        {
            return BadRequest(new { error = "Missing Stripe-Signature header" });
        }

        var rawPayload = await ReadRawBodyAsync(ct);

        var stripeSecret = options.Value.Stripe.WebhookSecret;
        if (string.IsNullOrEmpty(stripeSecret) && HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsEnvironment("Test"))
        {
            stripeSecret = "whsec_test";
        }

        if (string.IsNullOrEmpty(stripeSecret))
        {
            logger.LogError("Stripe webhook secret not configured");
            return StatusCode(500, new { error = "Webhook misconfigured" });
        }

        if (!StripeSignatureValidator.TryValidate(rawPayload, signature, stripeSecret))
        {
            logger.LogWarning("Rejected Stripe webhook: signature verification failed");
            return BadRequest(new { error = "Invalid signature" });
        }

        // Extract Stripe event metadata (id + type) from the raw payload.
        // Stripe's payload always carries top-level "id" and "type" fields.
        var (providerEventId, eventType) = ExtractStripeMetadata(rawPayload);
        if (string.IsNullOrEmpty(providerEventId) || string.IsNullOrEmpty(eventType))
        {
            return BadRequest(new { error = "Malformed Stripe payload" });
        }

        await PublishValidatedAsync("Stripe", providerEventId, eventType, rawPayload, signature, ct);

        // Return 200 to Stripe immediately. Downstream processing is the
        // consumer's responsibility; the outbox guarantees at-least-once
        // delivery, the inbox dedupes redeliveries.
        return Ok();
    }

    [HttpPost("paypal")]
    public async Task<IActionResult> PayPal(CancellationToken ct)
    {
        Request.EnableBuffering();
        // PayPal sends multiple signature headers; bundle them into one
        // JSON blob for the consumer to re-validate against PayPal's
        // verify-signature API. Inline validation is intentionally NOT
        // done here — PayPal's verification requires an out-of-band HTTP
        // call to PayPal API (with its own auth token), which would
        // double the webhook latency. Instead we accept the request,
        // store the validated-event row in the outbox, and the consumer
        // verifies against PayPal API on its own time.
        //
        // (Stripe HMAC verification IS done inline because it's local-only.)
        var signatureHeaders = JsonSerializer.Serialize(new
        {
            transmissionId   = Request.Headers["PAYPAL-TRANSMISSION-ID"].ToString(),
            transmissionTime = Request.Headers["PAYPAL-TRANSMISSION-TIME"].ToString(),
            transmissionSig  = Request.Headers["PAYPAL-TRANSMISSION-SIG"].ToString(),
            certUrl          = Request.Headers["PAYPAL-CERT-URL"].ToString(),
            authAlgo         = Request.Headers["PAYPAL-AUTH-ALGO"].ToString(),
            webhookId        = options.Value.PayPal.WebhookId,
        });

        // Reject obviously-broken requests up front (no auth-algo header at
        // all means the request didn't come from PayPal).
        if (string.IsNullOrEmpty(Request.Headers["PAYPAL-AUTH-ALGO"]))
        {
            return BadRequest(new { error = "Missing PAYPAL-AUTH-ALGO header" });
        }

        var rawPayload = await ReadRawBodyAsync(ct);

        var (providerEventId, eventType) = ExtractPayPalMetadata(rawPayload);
        if (string.IsNullOrEmpty(providerEventId) || string.IsNullOrEmpty(eventType))
        {
            return BadRequest(new { error = "Malformed PayPal payload" });
        }

        await PublishValidatedAsync("PayPal", providerEventId, eventType, rawPayload, signatureHeaders, ct);
        return Ok();
    }

    private async Task PublishValidatedAsync(
        string provider,
        string providerEventId,
        string eventType,
        string rawPayload,
        string signature,
        CancellationToken ct)
    {
        var evt = new PaymentWebhookValidatedEvent
        {
            Provider = provider,
            ProviderEventId = providerEventId,
            EventType = eventType,
            RawPayload = rawPayload,
            Signature = signature,
        };

        // MessageId == provider EventId. MassTransit's inbox uses MessageId
        // for dedupe — replays from Stripe/PayPal carry the same ProviderEventId,
        // so the consumer sees them as duplicates and skips processing.
        await publishEndpoint.Publish(evt, context =>
        {
            context.MessageId = DeterministicGuidFor(provider, providerEventId);
        }, ct);

        logger.LogInformation(
            "Webhook accepted: provider={Provider}, eventId={ProviderEventId}, type={EventType}",
            provider, providerEventId, eventType);
    }

    private async Task<string> ReadRawBodyAsync(CancellationToken ct)
    {
        // EnableBuffering would let us re-read; we only need it once.
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static (string id, string type) ExtractStripeMetadata(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            return (id ?? string.Empty, type ?? string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static (string id, string type) ExtractPayPalMetadata(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            // PayPal uses "id" and "event_type"
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var type = root.TryGetProperty("event_type", out var typeEl) ? typeEl.GetString() : null;
            return (id ?? string.Empty, type ?? string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Maps (provider, providerEventId) -> deterministic GUID so MT's
    /// inbox correctly identifies replays as duplicates regardless of the
    /// trace-level DomainEvent.EventId on the published message.
    /// </summary>
    private static Guid DeterministicGuidFor(string provider, string providerEventId)
    {
        var key = $"{provider}:{providerEventId}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        // First 16 bytes of SHA-256 form a v4-shaped GUID (top bits aren't
        // meaningful but MassTransit doesn't care about UUID variant).
        return new Guid(bytes[..16]);
    }
}
