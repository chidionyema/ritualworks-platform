using MassTransit;

namespace Haworks.Privacy.Application.Requests.Sagas;

public class PrivacyRequestState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = null!;
    public int Version { get; set; }

    public Guid UserId { get; set; }
    public string RequestType { get; set; } = null!;
    
    // Tracking completion of steps (simplistic for now)
    public bool IdentityCompleted { get; set; }
    public bool OrdersCompleted { get; set; }
    public bool PaymentsCompleted { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
