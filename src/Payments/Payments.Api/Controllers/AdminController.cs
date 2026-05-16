using Haworks.BuildingBlocks.Messaging;
using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
[Authorize(Roles = "Admin,Service")]
public sealed class AdminController(
    PaymentDbContext db,
    IDomainEventPublisher eventPublisher,
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

        // Same publish pattern catalog UpdateProductCommandHandler uses
        // (and which IS observed working end-to-end against real outbox + RabbitMQ):
        // IDomainEventPublisher.PublishAsync goes through MT's IPublishEndpoint
        // with an (object)-cast for runtime type resolution, then SaveChanges
        // commits the OutboxMessage row atomically.
        await eventPublisher.PublishAsync(demoEvent, ct);
        await db.SaveChangesAsync(ct);

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

    /// <summary>
    /// Pauses the MassTransit outbox relay for this payments-svc instance.
    /// Subsequent demo-event publishes still land in the OutboxMessage
    /// table, but BusOutboxDeliveryService can't dispatch them — the
    /// RelayPauseFilter on the bus's send pipeline throws while paused,
    /// keeping rows undelivered. Resume drains the backlog on the next
    /// 1s tick.
    /// </summary>
    [HttpPost("relay-pause")]
    public IActionResult PauseRelay()
    {
        RelayPauseGate.Pause();
        logger.LogWarning("MT outbox relay paused (payments-svc)");
        return Ok(new { paused = true });
    }

    /// <summary>Resume relay; queued OutboxMessage rows drain on the next tick.</summary>
    [HttpPost("relay-resume")]
    public IActionResult ResumeRelay()
    {
        RelayPauseGate.Resume();
        logger.LogWarning("MT outbox relay resumed (payments-svc)");
        return Ok(new { paused = false });
    }

    /// <summary>
    /// Reports current relay state + the live count of undelivered
    /// OutboxMessage rows. The count is a real query against the
    /// payments DB — no synthetic counters.
    /// </summary>
    [HttpGet("relay-status")]
    public async Task<IActionResult> RelayStatus(CancellationToken ct)
    {
        var queued = await db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>()
            .CountAsync(ct);
        return Ok(new
        {
            paused = RelayPauseGate.IsPaused,
            queuedMessages = queued,
        });
    }
}

public sealed record DemoEventRequest(Guid SessionId, string? Payload);
