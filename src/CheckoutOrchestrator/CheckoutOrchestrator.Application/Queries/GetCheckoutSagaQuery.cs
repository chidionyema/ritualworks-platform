using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.CheckoutOrchestrator.Application.Queries;

public sealed record GetCheckoutSagaQuery(Guid SagaId) : IRequest<Result<CheckoutSagaDto>>;

public sealed record GetCheckoutSagaByOrderIdQuery(Guid OrderId) : IRequest<Result<CheckoutSagaDto>>;

public sealed record CheckoutSagaDto(
    Guid SagaId,
    string CurrentState,
    Guid OrderId,
    Guid? PaymentId,
    string? PaymentCheckoutUrl,
    string? FailureReason,
    DateTime CreatedAt
);
