using MediatR;
using Microsoft.AspNetCore.Http;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] Guid? categoryId = null,
        CancellationToken ct = default)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        return (await mediator.Send(new ListProductsQuery(skip, take, categoryId), ct)).ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateProductCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

public sealed record ReserveStockRequest
{
    public required int Quantity { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid SagaId { get; init; }
    public required string UserId { get; init; }
    public required decimal TotalAmount { get; init; }
    public required string Currency { get; init; }
    public required string CustomerEmail { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record UpdateProductRequest
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required decimal UnitPrice { get; init; }
    public required Guid CategoryId { get; init; }
    public required bool IsListed { get; init; }
    public Guid? CorrelationId { get; init; }
}
