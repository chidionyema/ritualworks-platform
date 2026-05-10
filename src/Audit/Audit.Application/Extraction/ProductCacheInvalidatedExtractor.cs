using MassTransit;
using Haworks.Contracts.Catalog;

namespace Haworks.Audit.Application.Extraction;

public sealed class ProductCacheInvalidatedExtractor : BaseAuditExtractor<ProductCacheInvalidatedEvent>
{
    public override AuditRow Extract(ProductCacheInvalidatedEvent evt, ConsumeContext<ProductCacheInvalidatedEvent> ctx)
    {
        // Spec § 5.1: entity_type="cache", entity_id=CacheKey (using ProductId as key)
        return CreateRow(evt, ctx, "cache", evt.ProductId.ToString());
    }
}
