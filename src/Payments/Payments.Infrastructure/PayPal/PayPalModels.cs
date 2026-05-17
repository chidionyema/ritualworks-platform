using System.Text.Json;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// Thrown inside Polly delegates for non-transient PayPal HTTP errors (4xx except 429).
/// Caught outside the ExecuteAsync call to return a failure result without retry.
/// </summary>
public sealed class PayPalNonTransientException(string message) : Exception(message);

// ============================================================================
// PayPal Order API Models
// ============================================================================

/// <summary>
/// Request to create a PayPal checkout order.
/// </summary>
internal sealed class PayPalOrderRequest
{
    public string Intent { get; set; } = "CAPTURE";
    public List<PayPalPurchaseUnit> PurchaseUnits { get; set; } = new();
    public PayPalApplicationContext? ApplicationContext { get; set; }
}

/// <summary>
/// Purchase unit in a PayPal order.
/// </summary>
internal sealed class PayPalPurchaseUnit
{
    public string? ReferenceId { get; set; }
    public PayPalAmount? Amount { get; set; }
    public string? Description { get; set; }
    public string? CustomId { get; set; }
    public PayPalPayments? Payments { get; set; }
}

/// <summary>
/// Amount in a PayPal transaction.
/// </summary>
internal sealed class PayPalAmount
{
    public string CurrencyCode { get; set; } = "USD";
    public string Value { get; set; } = "0.00";
}

/// <summary>
/// Application context for PayPal checkout experience.
/// </summary>
internal sealed class PayPalApplicationContext
{
    public string? ReturnUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string? BrandName { get; set; }
    public string? UserAction { get; set; }
}

/// <summary>
/// PayPal order response.
/// </summary>
internal sealed class PayPalOrder
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public List<PayPalPurchaseUnit>? PurchaseUnits { get; set; }
    public PayPalPayer? Payer { get; set; }
    public List<PayPalLink>? Links { get; set; }
}

/// <summary>
/// PayPal order response with detailed payment info.
/// </summary>
internal sealed class PayPalOrderResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public List<PayPalPurchaseUnitResponse>? PurchaseUnits { get; set; }
}

/// <summary>
/// Purchase unit response with payment details.
/// </summary>
internal sealed class PayPalPurchaseUnitResponse
{
    public string? ReferenceId { get; set; }
    public string? CustomId { get; set; }
    public PayPalAmountResponse? Amount { get; set; }
    public PayPalPaymentsResponse? Payments { get; set; }
}

/// <summary>
/// Amount response from PayPal.
/// </summary>
internal sealed class PayPalAmountResponse
{
    public string? CurrencyCode { get; set; }
    public string? Value { get; set; }
}

/// <summary>
/// Payments collection in a purchase unit.
/// </summary>
internal sealed class PayPalPayments
{
    public List<PayPalCapture>? Captures { get; set; }
}

/// <summary>
/// Payments response with captures.
/// </summary>
internal sealed class PayPalPaymentsResponse
{
    public List<PayPalCaptureResponse>? Captures { get; set; }
}

/// <summary>
/// Capture details.
/// </summary>
internal sealed class PayPalCapture
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public PayPalAmount? Amount { get; set; }
}

/// <summary>
/// Capture response from PayPal.
/// </summary>
internal sealed class PayPalCaptureResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public PayPalAmountResponse? Amount { get; set; }
}

/// <summary>
/// PayPal payer information.
/// </summary>
internal sealed class PayPalPayer
{
    public string? PayerId { get; set; }
    public string? EmailAddress { get; set; }
}

/// <summary>
/// HATEOAS link from PayPal responses.
/// </summary>
internal sealed class PayPalLink
{
    public string? Href { get; set; }
    public string? Rel { get; set; }
    public string? Method { get; set; }
}

// ============================================================================
// PayPal Subscription API Models
// ============================================================================

/// <summary>
/// Request to create a PayPal subscription.
/// </summary>
internal sealed class PayPalSubscriptionRequest
{
    public string? PlanId { get; set; }
    public PayPalApplicationContext? ApplicationContext { get; set; }
    public PayPalSubscriber? Subscriber { get; set; }
}

/// <summary>
/// Subscriber information for subscriptions.
/// </summary>
internal sealed class PayPalSubscriber
{
    public string? EmailAddress { get; set; }
}

/// <summary>
/// PayPal subscription response.
/// </summary>
internal sealed class PayPalSubscription
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? PlanId { get; set; }
    public List<PayPalLink>? Links { get; set; }
}

// ============================================================================
// PayPal Refund API Models
// ============================================================================

/// <summary>
/// Request to refund a PayPal capture.
/// </summary>
internal sealed class PayPalRefundRequest
{
    public PayPalRefundAmount? Amount { get; set; }
    public string? NoteToPayer { get; set; }
    public string? CustomId { get; set; }
}

/// <summary>
/// Amount for a refund request.
/// </summary>
internal sealed class PayPalRefundAmount
{
    public string CurrencyCode { get; set; } = "USD";
    public string Value { get; set; } = "0.00";
}

/// <summary>
/// PayPal refund response.
/// </summary>
internal sealed class PayPalRefundResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public PayPalRefundAmount? Amount { get; set; }
    public PayPalStatusDetails? StatusDetails { get; set; }
}

/// <summary>
/// Status details for refunds.
/// </summary>
internal sealed class PayPalStatusDetails
{
    public string? Reason { get; set; }
}

// ============================================================================
// PayPal Error Models
// ============================================================================

/// <summary>
/// PayPal API error response.
/// </summary>
internal sealed class PayPalErrorResponse
{
    public string? Name { get; set; }
    public string? Message { get; set; }
    public List<PayPalErrorDetail>? Details { get; set; }
}

/// <summary>
/// Detail of a PayPal error.
/// </summary>
internal sealed class PayPalErrorDetail
{
    public string? Field { get; set; }
    public string? Description { get; set; }
}

// ============================================================================
// PayPal Webhook Models
// ============================================================================

/// <summary>
/// Request to verify a PayPal webhook signature.
/// </summary>
internal sealed class PayPalVerifySignatureRequest
{
    public string? WebhookId { get; set; }
    public string? TransmissionId { get; set; }
    public string? TransmissionTime { get; set; }
    public string? TransmissionSig { get; set; }
    public string? CertUrl { get; set; }
    public string? AuthAlgo { get; set; }
    public JsonDocument? WebhookEvent { get; set; }
}

/// <summary>
/// Response from PayPal webhook signature verification.
/// </summary>
internal sealed class PayPalVerifySignatureResponse
{
    public string? VerificationStatus { get; set; }
}

/// <summary>
/// Parsed PayPal signature headers from webhook request.
/// </summary>
internal sealed class PayPalSignatureHeaders
{
    public string TransmissionId { get; set; } = string.Empty;
    public string TransmissionTime { get; set; } = string.Empty;
    public string TransmissionSig { get; set; } = string.Empty;
    public string CertUrl { get; set; } = string.Empty;
    public string AuthAlgo { get; set; } = string.Empty;
}

/// <summary>
/// PayPal webhook event payload.
/// </summary>
internal sealed class PayPalWebhookEvent
{
    public string? Id { get; set; }
    public string? EventType { get; set; }
    public string? CreateTime { get; set; }
    public string? ResourceType { get; set; }
    public JsonElement? Resource { get; set; }
    public string? Summary { get; set; }
}

// ============================================================================
// PayPal OAuth Models
// ============================================================================

/// <summary>
/// PayPal OAuth2 token response.
/// </summary>
internal sealed class PayPalTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
}

// ============================================================================
// PayPal Subscription Management Models
// ============================================================================

/// <summary>
/// Request to cancel a PayPal subscription.
/// </summary>
internal sealed class PayPalCancelSubscriptionRequest
{
    public string? Reason { get; set; }
}

/// <summary>
/// PayPal subscription response from the API.
/// </summary>
internal sealed class PayPalSubscriptionResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? PlanId { get; set; }
    public PayPalBillingInfo? BillingInfo { get; set; }
    public PayPalSubscriber? Subscriber { get; set; }
}

/// <summary>
/// Billing information for a subscription.
/// </summary>
internal sealed class PayPalBillingInfo
{
    public string? NextBillingTime { get; set; }
    public string? LastPaymentTime { get; set; }
    public PayPalLastPayment? LastPayment { get; set; }
}

/// <summary>
/// Last payment information for a subscription.
/// </summary>
internal sealed class PayPalLastPayment
{
    public PayPalAmountResponse? Amount { get; set; }
    public string? Time { get; set; }
}
