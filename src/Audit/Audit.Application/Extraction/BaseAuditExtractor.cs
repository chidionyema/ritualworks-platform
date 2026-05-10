using System.Text.Json;
using MassTransit;
using Haworks.Contracts;

namespace Haworks.Audit.Application.Extraction;

public abstract class BaseAuditExtractor<T> : IAuditExtractor<T> where T : class, IDomainEvent
{
    public abstract AuditRow Extract(T evt, ConsumeContext<T> ctx);

    protected static AuditRow CreateRow(
        T evt, 
        ConsumeContext<T> ctx, 
        string entityType, 
        string entityId,
        string? actorId = null,
        string? actorType = null)
    {
        var metadataObj = new
        {
            publishedBy = ctx.SourceAddress?.ToString(),
            messageId = ctx.MessageId?.ToString()
        };

        return new AuditRow(
            OccurredAt: evt.OccurredAt,
            EventType: typeof(T).Name,
            EntityType: entityType,
            EntityId: entityId,
            ActorId: actorId,
            ActorType: actorType,
            CorrelationId: ctx.CorrelationId?.ToString(),
            Payload: JsonSerializer.SerializeToElement(evt),
            Metadata: JsonSerializer.SerializeToElement(metadataObj)
        );
    }
}
