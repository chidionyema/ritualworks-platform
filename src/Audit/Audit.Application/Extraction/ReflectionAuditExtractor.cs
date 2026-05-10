using MassTransit;
using Haworks.Contracts;

namespace Haworks.Audit.Application.Extraction;

public class ReflectionAuditExtractor<T> : BaseAuditExtractor<T> where T : class, IDomainEvent
{
    private static readonly string[] EntityIdCandidateProperties = 
        { "OrderId", "UserId", "PaymentId", "SkuId", "ProductId", "CartId" };

    public override AuditRow Extract(T evt, ConsumeContext<T> ctx)
    {
        var type = typeof(T);
        
        string entityType = "unknown";
        string entityId = string.Empty;

        foreach (var propName in EntityIdCandidateProperties)
        {
            var prop = type.GetProperty(propName);
            if (prop != null)
            {
                var val = prop.GetValue(evt);
                if (val != null)
                {
                    entityId = val.ToString() ?? string.Empty;
                    entityType = propName.EndsWith("Id", StringComparison.Ordinal) 
                        ? propName.Substring(0, propName.Length - 2).ToLowerInvariant() 
                        : propName.ToLowerInvariant();
                    break;
                }
            }
        }

        return CreateRow(evt, ctx, entityType, entityId);
    }
}
