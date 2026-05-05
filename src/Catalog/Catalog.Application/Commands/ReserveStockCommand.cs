using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;

namespace Haworks.Catalog.Application.Commands;

/// <summary>
/// Reserves stock on a single product and publishes <see cref="StockReservedEvent"/>.
///
/// In the eventual saga choreography (Phase 4+), checkout-svc emits a
/// <c>StockReservationRequestedEvent</c> consumed by catalog-svc, which then
/// emits <see cref="StockReservedEvent"/>. For Phase 2c we expose the same
/// flow as a synchronous REST call so we can prove out the per-context outbox
/// (xmin concurrency on Product + atomic outbox write) end-to-end without
/// needing checkout-svc to exist yet. The cross-context fields (OrderId,
/// SagaId, customer data, line items) are passed in by the caller — Phase 4
/// replaces this with a real <c>StockReservationRequestedEvent</c> consumer.
/// </summary>
public sealed record ReserveStockCommand(
    Guid ProductId,
    int Quantity,
    Guid OrderId,
    Guid SagaId,
    string UserId,
    decimal TotalAmount,
    string Currency,
    string CustomerEmail,
    string? IdempotencyKey) : IRequest<Result<Guid>>;


internal sealed class ReserveStockCommandHandler(
    IProductRepository products,
    IDomainEventPublisher eventPublisher,
    ILogger<ReserveStockCommandHandler> logger
) : IRequestHandler<ReserveStockCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ReserveStockCommand request, CancellationToken ct)
    {
        var product = await products.GetByIdTrackedAsync(request.ProductId, ct);
        if (product is null)
        {
            return Result.Failure<Guid>(Error.Products.NotFoundWithId(request.ProductId));
        }

        if (!product.ReserveStock(request.Quantity))
        {
            logger.LogWarning(
                "Stock reservation failed: insufficient stock for product {ProductId}. Requested {Requested}, available {Available}",
                product.Id, request.Quantity, product.StockQuantity);
            // Conflict (409) is the right HTTP semantic — the request is well-formed,
            // but the resource state (stock) won't satisfy it. Don't reuse
            // Error.Payment.InsufficientStock — that one is ErrorType.Internal
            // (500), wrong for a domain-level "no" answer.
            return Result.Failure<Guid>(Error.Conflict(
                "Stock.Insufficient",
                $"Insufficient stock for product {product.Id}: requested {request.Quantity}, available {product.StockQuantity}"));
        }

        // Publish BEFORE SaveChanges. With the per-context EF outbox the
        // publish writes a row to OutboxMessage in the active EF transaction;
        // SaveChangesAsync commits stock decrement + outbox row atomically.
        // The BusOutboxDeliveryService picks up the row and publishes to
        // RabbitMQ asynchronously.
        await eventPublisher.PublishAsync(new StockReservedEvent
        {
            OrderId = request.OrderId,
            SagaId = request.SagaId,
            UserId = request.UserId,
            TotalAmount = request.TotalAmount,
            Currency = request.Currency,
            CustomerEmail = request.CustomerEmail,
            IdempotencyKey = request.IdempotencyKey,
            Items = new[]
            {
                new Haworks.Contracts.Catalog.StockReservationItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Quantity = request.Quantity,
                    RemainingStock = product.StockQuantity
                }
            },
            OrderLineItems = new[]
            {
                new CheckoutItemData
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Quantity = request.Quantity,
                    UnitPrice = product.UnitPrice
                }
            }
        }, ct);

        try
        {
            await products.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the xmin race against a parallel reserver. The caller's
            // expectation is "I tried to reserve and someone else got there
            // first" — surface as 409 with a retry hint instead of letting
            // the framework produce a 500. Do NOT auto-retry inside the
            // handler: the saga (or REST caller) decides whether to retry
            // with the new stock state, which may mean a smaller quantity.
            logger.LogWarning(
                "Concurrent stock reservation conflict on product {ProductId}; caller should retry",
                request.ProductId);
            return Result.Failure<Guid>(Error.Conflict(
                "Stock.ConcurrencyConflict",
                $"Concurrent reservation on product {request.ProductId}; retry with the latest stock"));
        }

        logger.LogInformation(
            "Stock reserved: product {ProductId}, quantity {Quantity}, remaining {Remaining}, order {OrderId}",
            product.Id, request.Quantity, product.StockQuantity, request.OrderId);
        return Result.Success(product.Id);
    }
}
