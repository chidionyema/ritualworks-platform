namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Constants for Stripe API values.
/// </summary>
internal static class StripeConstants
{
    /// <summary>
    /// Stripe webhook event type constants.
    /// </summary>
    public static class EventTypes
    {
        public const string CheckoutSessionCompleted = "checkout.session.completed";
        public const string CheckoutSessionExpired = "checkout.session.expired";
        public const string CustomerSubscriptionCreated = "customer.subscription.created";
        public const string CustomerSubscriptionUpdated = "customer.subscription.updated";
        public const string CustomerSubscriptionDeleted = "customer.subscription.deleted";
        public const string InvoicePaymentFailed = "invoice.payment_failed";
        public const string ChargeRefunded = "charge.refunded";
    }

    /// <summary>
    /// Stripe checkout session modes.
    /// </summary>
    public static class SessionModes
    {
        public const string Payment = "payment";
        public const string Subscription = "subscription";
        public const string Setup = "setup";
    }

    /// <summary>
    /// Stripe payment method types.
    /// </summary>
    public static class PaymentMethods
    {
        public const string Card = "card";
    }

    /// <summary>
    /// Stripe checkout session statuses.
    /// </summary>
    public static class SessionStatuses
    {
        public const string Open = "open";
        public const string Complete = "complete";
        public const string Expired = "expired";
    }

    /// <summary>
    /// Stripe payment statuses.
    /// </summary>
    public static class PaymentStatuses
    {
        public const string Paid = "paid";
        public const string Unpaid = "unpaid";
        public const string NoPaymentRequired = "no_payment_required";
    }

    /// <summary>
    /// Common Stripe error codes.
    /// </summary>
    public static class ErrorCodes
    {
        public const string ResourceMissing = "resource_missing";
    }
}
