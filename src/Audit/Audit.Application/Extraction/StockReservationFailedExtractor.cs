using MassTransit;
using Haworks.Contracts.Catalog;

namespace Haworks.Audit.Application.Extraction;

public sealed class StockReservationFailedExtractor : BaseAuditExtractor<StockReservationFailedEvent>
{
    public override AuditRow Extract(StockReservationFailedEvent evt, ConsumeContext<StockReservationFailedEvent> ctx)
    {
        // Spec § 5.1: pick OrderId
        return CreateRow(evt, ctx, "order", evt.OrderId.ToString());
    }
}
