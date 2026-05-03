using MediatR;
using Microsoft.AspNetCore.Mvc;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Queries;

namespace Haworks.Catalog.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] Guid? categoryId = null,
        CancellationToken ct = default)
        => (await mediator.Send(new ListProductsQuery(skip, take, categoryId), ct)).ToActionResult();

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => (await mediator.Send(new GetProductByIdQuery(id), ct)).ToActionResult();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductCommand command, CancellationToken ct)
    {
        var result = await mediator.Send(command, ct);
        return result.ToCreatedActionResult(nameof(Get), new { id = result.IsSuccess ? result.Value : Guid.Empty });
    }

    [HttpPost("{id:guid}/reserve")]
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
