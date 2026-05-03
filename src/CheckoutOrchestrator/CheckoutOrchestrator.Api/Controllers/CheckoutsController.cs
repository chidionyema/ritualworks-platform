using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Haworks.Contracts.Checkout;

namespace Haworks.CheckoutOrchestrator.Api.Controllers;

/// <summary>
/// REST surface for the saga.
///   POST /api/checkouts                 — kick off a new saga (publishes
///                                          CheckoutInitiatedEvent; the
///                                          state machine takes over).
///   GET  /api/checkouts/{sagaId}        — ops/debug status query.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class CheckoutsController(
    IPublishEndpoint publishEndpoint,
    CheckoutDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartCheckoutRequest body, CancellationToken ct)
    {
        var sagaId = body.SagaId == Guid.Empty ? Guid.NewGuid() : body.SagaId;
        var orderId = body.OrderId == Guid.Empty ? Guid.NewGuid() : body.OrderId;

        await publishEndpoint.Publish(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = body.UserId,
            CustomerEmail = body.CustomerEmail,
            TotalAmount = body.TotalAmount,
            Items = body.Items,
            IdempotencyKey = body.IdempotencyKey,
            IsGuest = false,
        }, ct);

        return Accepted(new { sagaId, orderId });
    }

    [HttpGet("{sagaId:guid}")]
    public async Task<IActionResult> Get(Guid sagaId, CancellationToken ct)
    {
        var saga = await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId, ct);
        if (saga is null) return NotFound();

        return Ok(new
        {
            sagaId = saga.CorrelationId,
            currentState = saga.CurrentState,
            orderId = saga.OrderId,
            paymentId = saga.PaymentId,
            paymentCheckoutUrl = saga.PaymentCheckoutUrl,
            failureReason = saga.FailureReason,
            createdAt = saga.CreatedAt,
        });
    }
}

public sealed record StartCheckoutRequest(
    Guid SagaId,
    Guid OrderId,
    string UserId,
    string CustomerEmail,
    decimal TotalAmount,
    string IdempotencyKey,
    IReadOnlyList<CheckoutItemData> Items);
