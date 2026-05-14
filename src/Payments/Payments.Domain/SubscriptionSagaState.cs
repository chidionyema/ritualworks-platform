using MassTransit;

namespace Haworks.Payments.Domain;

public class SubscriptionSagaState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }   // SagaId = SubscriptionId
    public string CurrentState { get; set; } = "";
    public int Version { get; set; }

    public string ProviderSubscriptionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    
    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime PeriodEnd { get; set; }
    
    public Guid? RenewalTimeoutTokenId { get; set; }
    public Guid? DunningRetryTokenId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }
}
