using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Payments;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Commands.Refunds;

public sealed record CreateRefundCommand(
    Guid PaymentId, 
    decimal Amount, 
    string Currency, 
    string? Reason = null, 
    string? RequestedBy = null) : IRequest<Result<Guid>>;

public sealed class CreateRefundCommandHandler(
    IPaymentRepository paymentRepository,
    IDomainEventPublisher eventPublisher,
    ILogger<CreateRefundCommandHandler> logger) : IRequestHandler<CreateRefundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateRefundCommand request, CancellationToken ct)
    {
        var payment = await paymentRepository.GetByIdAsync(request.PaymentId, ct);
        if (payment == null)
        {
            return Result.Failure<Guid>(Error.NotFound("Payment.NotFound", $"Payment {request.PaymentId} not found"));
        }

        var refundId = Guid.NewGuid();

        await eventPublisher.PublishAsync(new RefundRequestedEvent
        {
            RefundId = refundId,
            OrderId = payment.OrderId,
            PaymentId = payment.Id,
            Amount = request.Amount,
            Currency = request.Currency,
            Reason = request.Reason,
            RequestedBy = request.RequestedBy ?? "Operator"
        }, ct);

        logger.LogInformation("Refund {RefundId} requested for Payment {PaymentId}", refundId, payment.Id);

        return Result.Success(refundId);
    }
}
