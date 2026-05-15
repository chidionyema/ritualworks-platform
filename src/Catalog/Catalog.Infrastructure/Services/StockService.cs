using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Haworks.Catalog.Infrastructure.Services;

internal sealed class StockService(
    CatalogDbContext db,
    ILogger<StockService> logger) : IStockService
{
    public async Task<Result> ReserveStockAsync(Guid orderId, IEnumerable<StockReservationItem> items, CancellationToken ct = default)
    {
        var itemsList = items.ToList();
        if (itemsList.Count == 0) return Result.Success();

        logger.LogInformation("Reserving stock for {Count} products for Order {OrderId}", itemsList.Count, orderId);

        var aggregatedItems = itemsList
            .GroupBy(i => i.ProductId)
            .Select(g => new StockReservationItem
            {
                ProductId = g.Key,
                ProductName = string.Empty,
                Quantity = g.Sum(i => i.Quantity)
            })
            .ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Check if reservation already exists — inside the transaction to
        // prevent TOCTOU race where two concurrent reservations both pass
        // the check before either inserts.
        var existing = await db.StockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (existing != null)
        {
            logger.LogWarning("Reservation for Order {OrderId} already exists", orderId);
            await transaction.RollbackAsync(ct);
            return Result.Success(); // Idempotent
        }

        try
        {
            foreach (var item in aggregatedItems)
            {
                if (item.Quantity <= 0) continue;

                var rowsAffected = await db.Products
                    .Where(p => p.Id == item.ProductId && p.StockQuantity >= item.Quantity)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity - item.Quantity)
                        .SetProperty(p => p.IsInStock, p => p.StockQuantity - item.Quantity > 0), ct);

                if (rowsAffected == 0)
                {
                    await transaction.RollbackAsync(ct);

                    var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == item.ProductId, ct);
                    if (product == null)
                        return Result.Failure(new Error("Stock.ProductNotFound", $"Product {item.ProductId} not found"));

                    return Result.Failure(new Error("Stock.InsufficientStock", $"Insufficient stock for {product.Name}. Available: {product.StockQuantity}, Requested: {item.Quantity}"));
                }
            }

            var itemsJson = JsonSerializer.Serialize(aggregatedItems);
            // Saga path: jump straight to Confirmed so observable behaviour
            // matches the legacy OrderStockReservation row (a row per order,
            // already-bound). The Pending lifecycle is reserved for B2's
            // sync flow, which uses IProductRepository.CreateReservationAsync.
            var reservation = StockReservation.CreateConfirmed(
                orderId,
                sagaId: Guid.Empty,
                userId: string.Empty,
                itemsJson);
            await db.StockReservations.AddAsync(reservation, ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stock reservation failed for Order {OrderId}", orderId);
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task ReleaseStockAsync(Guid orderId, string reason, CancellationToken ct = default)
    {
        var reservation = await db.StockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (reservation == null || reservation.ReleasedAt.HasValue) return;

        var items = JsonSerializer.Deserialize<List<StockReservationItem>>(reservation.ItemsJson) ?? new();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Atomically mark the reservation as released, guarded by
            // ReleasedAt IS NULL to prevent double-release races.
            var markedRows = await db.StockReservations
                .Where(r => r.Id == reservation.Id && r.ReleasedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.ReleasedAt, DateTime.UtcNow)
                    .SetProperty(r => r.ReleaseReason, reason), ct);

            if (markedRows == 0)
            {
                // Another thread already released this reservation.
                logger.LogWarning("Reservation for Order {OrderId} already released, skipping", orderId);
                await transaction.RollbackAsync(ct);
                return;
            }

            foreach (var item in items)
            {
                if (item.Quantity <= 0) continue;

                await db.Products
                    .Where(p => p.Id == item.ProductId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity + item.Quantity)
                        .SetProperty(p => p.IsInStock, true), ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stock release failed for Order {OrderId}", orderId);
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task ReleaseStockAsync(IEnumerable<StockReservationItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Count == 0) return;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in list)
            {
                if (item.Quantity <= 0) continue;

                await db.Products
                    .Where(p => p.Id == item.ProductId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity + item.Quantity)
                        .SetProperty(p => p.IsInStock, true), ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stock release (item-list overload) failed; rolling back");
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
