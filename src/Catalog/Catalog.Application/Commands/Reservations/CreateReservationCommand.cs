using MediatR;
using Microsoft.EntityFrameworkCore;
using Haworks.Catalog.Application.DTOs.Reservations;
using Haworks.Contracts.Catalog;
using Haworks.BuildingBlocks.Idempotency;

namespace Haworks.Catalog.Application.Commands.Reservations;

/// <summary>
/// Sync reservation create (ADR-004 phase 4 / B2). Decrements per-product
/// stock atomically and inserts a Pending <see cref="StockReservation"/>
/// row with a TTL. The repository's
/// <c>CreateReservationAsync</c> wraps both in a single transaction.
///
/// • <see cref="UserId"/> — caller-resolved (BFF-forwarded
///   <c>X-User-Id</c> in production; <c>guest</c> for anonymous flows).
/// • <see cref="ClientIdempotencyKey"/> — propagated as metadata only.
///   The platform's <c>IdempotencyMiddleware</c> handles HTTP-layer
///   replay protection via <c>X-Idempotency-Key</c>; this field is for
///   downstream tracing.
/// </summary>
public sealed record CreateReservationCommand(
    IReadOnlyList<ReservationItemDto> Items,
    string UserId,
    string? ClientIdempotencyKey,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<ReservationDto>>;


internal sealed class CreateReservationCommandHandler(
    IProductRepository products,
    ILogger<CreateReservationCommandHandler> logger)
    : IRequestHandler<CreateReservationCommand, Result<ReservationDto>>
{
    /// <summary>15-minute hold per ADR-004; matches monolith's pre-order TTL.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(15);

    public async Task<Result<ReservationDto>> Handle(
        CreateReservationCommand request,
        CancellationToken ct)
    {
        var contractItems = request.Items
            .Select(i => new StockReservationItem
            {
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
            })
            .ToList();

        try
        {
            var reservation = await products.CreateReservationAsync(
                request.UserId,
                contractItems,
                ReservationTtl,
                ct);

            logger.LogInformation(
                "Reservation {ReservationId} created for user {UserId}: {ItemCount} item(s), expires {ExpiresAt:o}",
                reservation.Id, request.UserId, contractItems.Count, reservation.ExpiresAt);

            var dto = new ReservationDto(
                reservation.Id,
                request.Items,
                new DateTimeOffset(reservation.ExpiresAt, TimeSpan.Zero),
                IsExisting: false);

            return Result.Success(dto);
        }
        catch (Haworks.Catalog.Domain.InsufficientStockException ex)
        {
            // Map the domain exception to a typed error so the controller
            // can surface 409 without leaking exception details. Don't reuse
            // Error.Payment.InsufficientStock — that's ErrorType.Internal
            // and would map to 500.
            logger.LogWarning(
                "Insufficient stock for product {ProductId}: requested {Requested}, available {Available}",
                ex.ProductId, ex.Requested, ex.Available);
            return Result.Failure<ReservationDto>(Error.Conflict(
                "Reservation.InsufficientStock",
                ex.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the xmin race against a parallel reserver. Surface as
            // 409 with a retry hint instead of a 500.
            logger.LogWarning(
                "Concurrent reservation conflict for user {UserId}; caller should retry",
                request.UserId);
            return Result.Failure<ReservationDto>(Error.Conflict(
                "Reservation.ConcurrencyConflict",
                "Concurrent reservation; retry with the latest stock state."));
        }
    }
}
