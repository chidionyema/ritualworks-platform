using Haworks.Payments.Domain;
using Stripe.Checkout;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Stripe-specific validation helpers.
/// </summary>
internal static class StripeValidationHelper
{
    /// <summary>
    /// Validates that the Stripe session metadata matches the payment record.
    /// </summary>
    public static bool ValidateSessionMetadata(Session session, Payment paymentRecord)
    {
        if (session.Metadata == null)
        {
            return false;
        }

        if (!session.Metadata.TryGetValue("orderId", out var sessionOrderId))
        {
            return false;
        }

        return sessionOrderId == paymentRecord.OrderId.ToString();
    }
}
