using System.Text.Json;
using MediatR;
using Haworks.Catalog.Application.DTOs.Reservations;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Idempotency;

namespace Haworks.Catalog.Application.Commands.Reservations;

/// <summary>
/// Confirms a Pending <see cref="StockReservation"/> and binds it to a
/// server-issued <c>OrderId</c> + <c>SagaId</c>. Per
/// ADR-004 phase 4, the confirm step is the integration boundary
/// between the sync reservation flow and the saga: it publishes
/// <see cref="StockReservedEvent"/> so downstream consumers
/// (PaymentSessionConsumer, CheckoutSaga) can take over.
///
/// Domain failures from <see cref="StockReservation.Confirm"/>:
/// • Reservation not found              -> <c>Reservation.NotFound</c> (404)
/// • Status not Pending and expired     -> <c>Reservation.Expired</c> (410)
/// • Status not Pending and not expired -> <c>Reservation.InvalidState</c> (409)
/// </summary>
public sealed record ConfirmReservationCommand(
    Guid ReservationId,
    string UserId,
    string CustomerEmail,
    decimal TotalAmount,
    string Currency,
    string? ClientIdempotencyKey,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<ConfirmReservationResultDto>>;


internal sealed class ConfirmReservationCommandHandler(
    IProductRepository products,
    IDomainEventPublisher eventPublisher,
    ILogger<ConfirmReservationCommandHandler> logger)
    : IRequestHandler<ConfirmReservationCommand, Result<ConfirmReservationResultDto>>
{
    public async Task<Result<ConfirmReservationResultDto>> Handle(
        ConfirmReservationCommand request,
        CancellationToken ct)
    {
        var reservation = await products.GetReservationByIdTrackedAsync(request.ReservationId, ct);
        if (reservation is null)
        {
            return Result.Failure<ConfirmReservationResultDto>(Error.NotFound(
                "Reservation.NotFound",
                $"Reservation {request.ReservationId} not found."));
        }

        if (!string.Equals(reservation.UserId, request.UserId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Reservation {ReservationId} ownership mismatch: expected={OwnerId} actual={CallerId}",
                reservation.Id, reservation.UserId, request.UserId);
            return Result.Failure<ConfirmReservationResultDto>(Error.Forbidden(
                "Reservation.Forbidden",
                "You do not own this reservation."));
        }

        // Server-issued — ADR-004 phase 4 says the confirm step allocates
        // both ids so the caller's request body cannot poison either.
        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        if (!reservation.Confirm(orderId, sagaId))
        {
            // Two reasons Confirm() returns false: status wasn't Pending,
            // or the TTL elapsed. Disambiguate so the controller can map
            // 410 vs 409 cleanly.
            if (reservation.Status == ReservationStatus.Pending
                && DateTime.UtcNow > reservation.ExpiresAt)
            {
                logger.LogInformation(
                    "Reservation {ReservationId} expired at {ExpiresAt:o}; rejecting confirm",
                    reservation.Id, reservation.ExpiresAt);
                return Result.Failure<ConfirmReservationResultDto>(new Error(
                    "Reservation.Expired",
                    "Reservation has expired.",
                    ErrorType.Conflict));
            }

            logger.LogInformation(
                "Reservation {ReservationId} cannot be confirmed: status={Status}",
                reservation.Id, reservation.Status);
            return Result.Failure<ConfirmReservationResultDto>(Error.Conflict(
                "Reservation.InvalidState",
                $"Reservation status is {reservation.Status}; only Pending may be confirmed."));
        }

        // Rehydrate the line items from the JSON the create path stored.
        // Used to populate StockReservedEvent so downstream contexts have
        // the full reservation snapshot without crossing into Catalog.
        var storedItems = JsonSerializer
            .Deserialize<List<StockReservationItem>>(reservation.ItemsJson)
            ?? new List<StockReservationItem>();

        // Publish before SaveChanges so the per-context outbox commits the
        // status transition + the event row in one transaction.
        await eventPublisher.PublishAsync(new StockReservedEvent
        {
            OrderId = orderId,
            SagaId = sagaId,
            UserId = request.UserId,
            TotalAmount = request.TotalAmount,
            Currency = request.Currency,
            CustomerEmail = request.CustomerEmail,
            IdempotencyKey = request.ClientIdempotencyKey,
            Items = storedItems,
            OrderLineItems = storedItems.Select(i => new CheckoutItemData
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = 0m, // Sync flow does not capture per-line prices.
            }).ToList(),
        }, ct);

        await products.SaveChangesAsync(ct);

        logger.LogInformation(
            "Reservation {ReservationId} confirmed: orderId={OrderId} sagaId={SagaId}",
            reservation.Id, orderId, sagaId);

        return Result.Success(new ConfirmReservationResultDto(reservation.Id, orderId, sagaId));
    }
}
