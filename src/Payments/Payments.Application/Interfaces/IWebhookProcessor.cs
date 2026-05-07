using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Validates and processes incoming webhooks from payment providers.
/// Each provider implementation handles its specific signature validation
/// and event parsing.
/// </summary>
public interface IWebhookProcessor
{
    /// <summary>
    /// The provider this processor handles.
    /// </summary>
    PaymentProvider Provider { get; }

    /// <summary>
    /// Validates the webhook signature and parses the event.
    /// </summary>
    /// <param name="payload">Raw webhook payload</param>
    /// <param name="signature">Signature header value</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Parsed event or failure result</returns>
    Task<WebhookValidationResult> ValidateAndParseAsync(
        string payload,
        string signature,
        CancellationToken ct = default);

    /// <summary>
    /// Processes a validated webhook event.
    /// </summary>
    Task<WebhookProcessingResult> ProcessEventAsync(
        PaymentWebhookEvent webhookEvent,
        CancellationToken ct = default);
}

/// <summary>
/// Result of webhook signature validation.
/// </summary>
public record WebhookValidationResult
{
    public required bool IsValid { get; init; }
    public PaymentWebhookEvent? Event { get; init; }
    public string? ErrorMessage { get; init; }

    public static WebhookValidationResult Success(PaymentWebhookEvent evt) =>
        new() { IsValid = true, Event = evt };

    public static WebhookValidationResult Failure(string message) =>
        new() { IsValid = false, ErrorMessage = message };
}

/// <summary>
/// A parsed webhook event from a payment provider.
/// </summary>
public record PaymentWebhookEvent
{
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required PaymentProvider Provider { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Provider-specific event data. Cast in handler based on EventType.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Raw payload for audit/debugging.
    /// </summary>
    public string RawPayload { get; init; } = string.Empty;
}

/// <summary>
/// Result of processing a webhook event.
/// </summary>
public record WebhookProcessingResult
{
    public required bool Processed { get; init; }
    public string? EventType { get; init; }
    public string? Message { get; init; }

    public static WebhookProcessingResult Success(string eventType, string? message = null) =>
        new() { Processed = true, EventType = eventType, Message = message };

    public static WebhookProcessingResult Skipped(string reason) =>
        new() { Processed = false, Message = reason };

    public static WebhookProcessingResult Failed(string reason) =>
        new() { Processed = false, Message = reason };
}
