using MassTransit;

namespace Haworks.Payments.Domain;

public class RefundSagaState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }   // SagaId = RefundId
    public string CurrentState { get; set; } = "";
    public int Version { get; set; }

    public Guid OrderId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid RefundId { get; set; }        // mirrored from CorrelationId for clarity
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Reason { get; set; } = "";  // customer-cited reason, free-form
    public string Provider { get; set; } = ""; // "Stripe" | "PayPal"
    public string? ProviderRefundId { get; set; }  // populated post-ProviderRefundInitiated
    public string? FailureDetail { get; set; }
    public RefundFailureCategory FailureCategory { get; set; }
    public Guid? RefundTimeoutTokenId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
