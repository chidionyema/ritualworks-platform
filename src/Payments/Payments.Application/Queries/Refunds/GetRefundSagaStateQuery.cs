using Haworks.BuildingBlocks.Common;
using Haworks.Payments.Domain;
using Haworks.Payments.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Application.Queries.Refunds;

public sealed record GetRefundSagaStateQuery(Guid RefundId) : IRequest<Result<RefundSagaDto>>;

public sealed record RefundSagaDto(
    Guid RefundId,
    Guid OrderId,
    Guid PaymentId,
    string Status,
    decimal Amount,
    string Currency,
    string Reason,
    string Provider,
    string? ProviderRefundId,
    string? FailureDetail,
    string FailureCategory,
    DateTime CreatedAt);

internal sealed class GetRefundSagaStateQueryHandler(
    IPaymentDbContext db) : IRequestHandler<GetRefundSagaStateQuery, Result<RefundSagaDto>>
{
    public async Task<Result<RefundSagaDto>> Handle(GetRefundSagaStateQuery request, CancellationToken ct)
    {
        var saga = await db.RefundSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == request.RefundId, ct);

        if (saga == null) return Result.Failure<RefundSagaDto>(Error.NotFound("Refund.NotFound", "Refund saga not found"));

        return Result.Success(new RefundSagaDto(
            saga.RefundId,
            saga.OrderId,
            saga.PaymentId,
            saga.CurrentState,
            saga.Amount,
            saga.Currency,
            saga.Reason,
            saga.Provider,
            saga.ProviderRefundId,
            saga.FailureDetail,
            saga.FailureCategory.ToString(),
            saga.CreatedAt));
    }
}
