using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payments.Api.Controllers;

/// <summary>
/// Operational endpoints for payments-svc — exposed for the portfolio
/// site's demo flow via BffWeb. NOT part of payments' user-facing surface;
/// in production these MUST be locked behind a localhost-only or
/// mesh-only middleware (TODO: layer guard before prod deploy).
///
/// AllowAnonymous + minimal — same pattern as Catalog.Api/DemoTestController
/// and Identity.Api/AdminController.
/// </summary>
[ApiController]
[Route("admin")]
[AllowAnonymous]
public sealed class AdminController(
    PaymentDbContext db,
    IPublishEndpoint publishEndpoint,
    ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>
    /// T2.5's event-flow demo entry point. Begins a transaction on the
    /// payments DbContext, publishes a <see cref="DemoOutboxEvent"/> via
    /// the per-context EF outbox (so the OutboxMessage row commits
    /// atomically with whatever DB change might happen alongside — none
    /// here, but the transactional guarantee shape is the demo), and
    /// commits. The MassTransit relay drains the outbox row to RabbitMQ;
    /// BffWeb's DemoOutboxEventConsumer translates the inbound to a
    /// SignalR OnEventFlow stage='consumed' push so the frontend animates
    /// the persisted -> consumed lifecycle.
    /// </summary>
    [HttpPost("demo-event")]
    public async Task<IActionResult> PublishDemoEvent(
        [FromBody] DemoEventRequest request,
        CancellationToken ct)
    {
        var sessionId = request.SessionId == Guid.Empty ? Guid.NewGuid() : request.SessionId;
        var demoEvent = new DemoOutboxEvent
        {
            SessionId = sessionId,
            Payload = request.Payload,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Publish through the EF outbox filter that's wired in
        // Payments.Infrastructure DI. The OutboxMessage row is staged in
        // the same DbContext transaction; if SaveChanges fails the publish
        // never reaches the broker.
        await publishEndpoint.Publish(demoEvent, ctx =>
        {
            ctx.MessageId = demoEvent.EventId;
        }, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Demo event published via payments outbox: sessionId={SessionId}, eventId={EventId}",
            sessionId, demoEvent.EventId);

        return Accepted(new
        {
            sessionId,
            eventId = demoEvent.EventId,
            status = "Persisted",
        });
    }
}

public sealed record DemoEventRequest(Guid SessionId, string? Payload);
