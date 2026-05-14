namespace Haworks.Payments.Domain;

public enum RefundFailureCategory
{
    None = 0,
    ProviderRefundFailed,
    RefundTimedOut,
    CancelledByOperator,
}
