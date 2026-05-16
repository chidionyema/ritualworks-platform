using Haworks.BuildingBlocks.Common;
using Haworks.CheckoutOrchestrator.Application.Queries;
using Haworks.CheckoutOrchestrator.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.CheckoutOrchestrator.Application.QueryHandlers;

internal sealed class GetCheckoutSagaQueryHandler(ICheckoutDbContext db) : IRequestHandler<GetCheckoutSagaQuery, Result<CheckoutSagaDto>>
{
    public async Task<Result<CheckoutSagaDto>> Handle(GetCheckoutSagaQuery request, CancellationToken ct)
    {
        var saga = await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == request.SagaId, ct);

        if (saga is null) return Result.Failure<CheckoutSagaDto>(Error.NotFound("CheckoutSaga.NotFound", "Saga not found."));

        if (!request.IsAdmin && !string.Equals(saga.UserId, request.UserId, StringComparison.Ordinal))
            return Result.Failure<CheckoutSagaDto>(Error.Forbidden("CheckoutSaga.Forbidden", "You are not authorized to view this saga."));

        return Result.Success(new CheckoutSagaDto(
            saga.CorrelationId,
            saga.CurrentState,
            saga.OrderId,
            saga.PaymentId,
            saga.PaymentCheckoutUrl,
            saga.FailureReason,
            saga.CreatedAt
        ));
    }
}

internal sealed class GetCheckoutSagaByOrderIdQueryHandler(ICheckoutDbContext db) : IRequestHandler<GetCheckoutSagaByOrderIdQuery, Result<CheckoutSagaDto>>
{
    public async Task<Result<CheckoutSagaDto>> Handle(GetCheckoutSagaByOrderIdQuery request, CancellationToken ct)
    {
        var saga = await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrderId == request.OrderId, ct);

        if (saga is null) return Result.Failure<CheckoutSagaDto>(Error.NotFound("CheckoutSaga.NotFound", "Saga not found."));

        if (!request.IsAdmin && !string.Equals(saga.UserId, request.UserId, StringComparison.Ordinal))
            return Result.Failure<CheckoutSagaDto>(Error.Forbidden("CheckoutSaga.Forbidden", "You are not authorized to view this saga."));

        return Result.Success(new CheckoutSagaDto(
            saga.CorrelationId,
            saga.CurrentState,
            saga.OrderId,
            saga.PaymentId,
            saga.PaymentCheckoutUrl,
            saga.FailureReason,
            saga.CreatedAt
        ));
    }
}