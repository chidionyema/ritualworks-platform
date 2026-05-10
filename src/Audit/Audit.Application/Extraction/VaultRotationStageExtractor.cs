using MassTransit;
using Haworks.Contracts.Identity;

namespace Haworks.Audit.Application.Extraction;

public sealed class VaultRotationStageExtractor : BaseAuditExtractor<VaultRotationStageEvent>
{
    public override AuditRow Extract(VaultRotationStageEvent evt, ConsumeContext<VaultRotationStageEvent> ctx)
    {
        // Spec § 5.1: entity_type="system", entity_id=ServiceName
        // ServiceName is identity-svc for this event.
        return CreateRow(evt, ctx, "system", "identity-svc", actorId: "system", actorType: "system");
    }
}
