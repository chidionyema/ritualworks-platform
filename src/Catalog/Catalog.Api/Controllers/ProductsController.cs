using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Application.Queries;

namespace Haworks.Catalog.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ProductsController(
    IMediator mediator,
    IProductCacheReader productCache) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] Guid? categoryId = null,
        CancellationToken ct = default)
        => (await mediator.Send(new ListProductsQuery(skip, take, categoryId), ct)).ToActionResult();

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetProductByIdQuery(id), ct)).ToActionResult();

    /// <summary>
    /// Read a product through the HybridCache read-through layer. Returns
    /// the same DTO as <see cref="Get"/> plus a cache info envelope with the
    /// observed source (<c>L1</c> or <c>database</c>) and round-trip latency.
    /// Surfaces what the production GET already does internally; intended
    /// for the portfolio-site cache demo, but the bytes are real and the
    /// underlying call hits real Postgres on cache miss.
    /// </summary>
    [HttpGet("{id:guid}/cached")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCached(Guid id, CancellationToken ct)
    {
        var result = await productCache.GetAsync(id, ct);
        if (result.Product is null)
        {
            return NotFound(new { id, source = result.Source, latencyMs = result.LatencyMs });
        }
        return Ok(new
        {
            product = result.Product,
            source = result.Source,
            latencyMs = result.LatencyMs,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductRequest body, CancellationToken ct)
    {
        var command = new UpdateProductCommand(
            id,
            body.Name,
            body.Description,
            body.UnitPrice,
            body.CategoryId,
            body.IsListed,
            body.CorrelationId);
        return (await mediator.Send(command, ct)).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] Guid? correlationId,
        CancellationToken ct)
    {
        var command = new DeleteProductCommand(id, correlationId);
        return (await mediator.Send(command, ct)).ToActionResult();
    }

    [HttpPost("{id:guid}/reserve")]
    [Authorize(Roles = "Admin,Service")]
    public async Task<IActionResult> Reserve(Guid id, [FromBody] ReserveStockRequest body, CancellationToken ct)
    {
        var command = new ReserveStockCommand(
            id,
            body.Quantity,
            body.OrderId,
            body.SagaId,
            body.UserId,
            body.TotalAmount,
            body.Currency,
            body.CustomerEmail,
            body.IdempotencyKey);

        return (await mediator.Send(command, ct)).ToActionResult();
    }
}

public sealed record ReserveStockRequest(
    int Quantity,
    Guid OrderId,
    Guid SagaId,
    string UserId,
    decimal TotalAmount,
    string Currency,
    string CustomerEmail,
    string? IdempotencyKey);

public sealed record UpdateProductRequest(
    string Name,
    string Description,
    decimal UnitPrice,
    Guid CategoryId,
    bool IsListed,
    Guid? CorrelationId);
