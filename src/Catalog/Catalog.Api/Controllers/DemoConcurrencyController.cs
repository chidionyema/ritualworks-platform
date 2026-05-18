using Haworks.Catalog.Domain;
using Haworks.Catalog.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Catalog.Api.Controllers;

/// <summary>
/// Demo-only inventory endpoints for the portfolio's ConcurrencyDemo.
/// Reads/writes the same Product entity the real catalog uses, going
/// through EF Core with Postgres' xmin column as the concurrency
/// token (configured in CatalogDbContext line ~93). The PUT exposes
/// the same optimistic-concurrency mechanism that ReserveStockCommand
/// relies on under load — visitor sees real DbUpdateConcurrencyException
/// behaviour, not a simulation.
///
/// Endpoint shape matches the prior in-process BffWeb DemoController
/// inventory routes so the portfolio frontend wire shape doesn't change.
///
/// Pause postgres → SaveChanges throws → endpoint returns 503. Real
/// chaos surface for this demo.
/// </summary>
[ApiController]
[Route("demo/inventory")]
[AllowAnonymous]
public sealed class DemoConcurrencyController(
    CatalogDbContext db,
    ILogger<DemoConcurrencyController> logger) : ControllerBase
{
    [HttpGet("{productId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(Guid productId, CancellationToken ct)
    {
        try
        {
            // Project the xmin shadow property in the same SELECT so we
            // get the version + state in one round-trip without manually
            // managing the underlying DbConnection.
            var snapshot = await db.Products
                .AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.StockQuantity,
                    Version = EF.Property<uint>(p, "xmin"),
                })
                .FirstOrDefaultAsync(ct);
            if (snapshot is null) return NotFound(new { productId });

            return Ok(new
            {
                productId = snapshot.Id,
                inventory = new
                {
                    id = snapshot.Id.ToString(),
                    name = snapshot.Name,
                    quantity = snapshot.StockQuantity,
                    version = snapshot.Version.ToString(),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Inventory read failed for {ProductId}", productId);
            return StatusCode(500, new { error = "An internal error occurred" });
        }
    }

    [HttpPut("{productId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid productId,
        [FromBody] InventoryUpdate update,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return NotFound(new { productId });

        // If the client provided an If-Match header, prime EF's concurrency
        // check by setting OriginalValue on the xmin shadow property. The
        // generated UPDATE becomes "WHERE Id = @id AND xmin = @providedXmin"
        // — a concurrent updater that already incremented xmin will cause
        // this one to throw DbUpdateConcurrencyException at SaveChanges
        // time, which we translate to 412 Precondition Failed.
        if (!string.IsNullOrWhiteSpace(ifMatch) && uint.TryParse(ifMatch, out var providedVersion))
        {
            db.Entry(product).Property<uint>("xmin").OriginalValue = providedVersion;
        }

        // Apply the requested adjustment through the domain methods so
        // invariants (no negative stock) are enforced. RestockTo replaces
        // the value; the demo client sends the absolute new quantity.
        if (update.Quantity < 0)
        {
            return BadRequest(new { error = "quantity must be >= 0" });
        }
        product.RestockTo(update.Quantity);

        try
        {
            await db.SaveChangesAsync(ct);
            var newVersion = await ReadXminAsync(productId, ct);
            return Ok(new
            {
                productId = product.Id,
                inventory = new
                {
                    id = product.Id.ToString(),
                    name = product.Name,
                    quantity = product.StockQuantity,
                    version = newVersion?.ToString() ?? "0",
                },
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer beat us. Reload to surface the current
            // version + quantity in the conflict response so the client
            // can retry with the right baseline.
            db.Entry(product).State = EntityState.Detached;
            var current = await db.Products
                .AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.StockQuantity,
                    Version = EF.Property<uint>(p, "xmin"),
                })
                .FirstOrDefaultAsync(ct);
            return StatusCode(412, new
            {
                error = "stale_version",
                message = "Another writer updated this product since you read it.",
                currentInventory = current is null ? null : new
                {
                    id = current.Id.ToString(),
                    name = current.Name,
                    quantity = current.StockQuantity,
                    version = current.Version.ToString(),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Inventory update failed for {ProductId}", productId);
            return StatusCode(500, new { error = "An internal error occurred" });
        }
    }

    private async Task<uint?> ReadXminAsync(Guid productId, CancellationToken ct)
    {
        // Project xmin alongside other columns; EF manages the connection
        // lifecycle, no manual GetDbConnection() (which can race with EF's
        // own DbContext-scoped connection).
        var snap = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { Version = EF.Property<uint>(p, "xmin") })
            .FirstOrDefaultAsync(ct);
        return snap?.Version;
    }

    public sealed record InventoryUpdate
    {
        public required int Quantity { get; init; }
    }
}
