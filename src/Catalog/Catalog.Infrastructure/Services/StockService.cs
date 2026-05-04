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

        // Check if reservation already exists
        var existing = await db.OrderStockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (existing != null)
        {
            logger.LogWarning("Reservation for Order {OrderId} already exists", orderId);
            return Result.Success(); // Idempotent
        }

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

        try
        {
            foreach (var item in aggregatedItems)
            {
                if (item.Quantity <= 0) continue;

                var rowsAffected = await db.Products
                    .Where(p => p.Id == item.ProductId && p.StockQuantity >= item.Quantity)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity - item.Quantity)
                        .SetProperty(p => p.IsInStock, p => p.StockQuantity > item.Quantity), ct);

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
            var reservation = OrderStockReservation.Create(orderId, itemsJson);
            await db.OrderStockReservations.AddAsync(reservation, ct);
            
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
        var reservation = await db.OrderStockReservations.FirstOrDefaultAsync(r => r.OrderId == orderId, ct);
        if (reservation == null || reservation.ReleasedAt.HasValue) return;

        var items = JsonSerializer.Deserialize<List<StockReservationItem>>(reservation.ItemsJson) ?? new();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                if (item.Quantity <= 0) continue;

                await db.Products
                    .Where(p => p.Id == item.ProductId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.StockQuantity, p => p.StockQuantity + item.Quantity)
                        .SetProperty(p => p.IsInStock, true), ct);
            }

            reservation.MarkReleased(reason);
            db.OrderStockReservations.Update(reservation);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stock release failed for Order {OrderId}", orderId);
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
