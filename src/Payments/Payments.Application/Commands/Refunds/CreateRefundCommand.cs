using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Payments;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Commands.Refunds;

public sealed record CreateRefundCommand(
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string IdempotencyKey,
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

        if (payment.Status != Domain.PaymentStatus.Completed && payment.Status != Domain.PaymentStatus.Refunded)
        {
            return Result.Failure<Guid>(Error.Validation("Payment.NotCompleted", $"Payment must be completed before refund. Current status: {payment.Status}"));
        }

        var refundId = Guid.NewGuid();

        // Mutate domain state first — RecordRefund validates remaining amount
        // and throws if total would exceed payment amount.
        try
        {
            payment.RecordRefund(request.Amount);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Refund.InvalidState", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Refund.InvalidAmount", ex.Message));
        }

        // Publish BEFORE SaveChanges so both the entity mutation and the outbox
        // message commit atomically in the same transaction via MassTransit EF Outbox.
        // The Payment entity carries an xmin concurrency token — EF will throw
        // DbUpdateConcurrencyException if another refund raced us.
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

        try
        {
            await paymentRepository.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Concurrent refund detected for Payment {PaymentId}; rejecting", payment.Id);
            return Result.Failure<Guid>(Error.Conflict("Refund.ConcurrencyConflict",
                "Another refund for this payment was processed concurrently. Please retry."));
        }

        logger.LogInformation("Refund {RefundId} requested for Payment {PaymentId}", refundId, payment.Id);

        return Result.Success(refundId);
    }
}
