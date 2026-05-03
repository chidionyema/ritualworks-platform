namespace Haworks.Payments.Domain;

/// <summary>
/// External payment gateways supported by payments-svc. Used to dispatch
/// webhook payload parsing to the right provider processor.
/// </summary>
public enum PaymentProvider
{
    None = 0,
    Stripe = 1,
    PayPal = 2,
    Square = 3,
    Braintree = 4,
}
