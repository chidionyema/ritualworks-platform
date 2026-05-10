using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Audit.Application.Queries;
using Haworks.Audit.Api.Models;

namespace Haworks.Audit.Api.Controllers;

[ApiController]
[Route("audit/events")]
[Authorize(Roles = "audit-reader")]
public class AuditQueryController : ControllerBase
{
    private readonly IAuditQueryService _queryService;

    public AuditQueryController(IAuditQueryService queryService)
    {
        _queryService = queryService;
    }

    [HttpGet]
    public async Task<ActionResult<AuditPageResponse<AuditEventDto>>> ListEvents(
        [FromQuery] string? entityType,
        [FromQuery] string? entityId,
        [FromQuery] string? eventType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _queryService.ListAsync(new AuditQueryRequest(
            entityType, entityId, eventType, from, to, cursor, limit), ct);
            
        return Ok(new AuditPageResponse<AuditEventDto>(
            result.Items.Select(e => new AuditEventDto(
                e.Id, e.OccurredAt, e.EventType, e.EntityType, e.EntityId,
                e.ActorId, e.ActorType, e.CorrelationId, e.Payload.RootElement, e.Metadata.RootElement)),
            result.NextCursor));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuditEventDto>> GetEvent(Guid id, [FromQuery] DateTimeOffset occurredAt, CancellationToken ct)
    {
        var e = await _queryService.GetByIdAsync(id, occurredAt, ct);
        if (e == null) return NotFound();
        
        return Ok(new AuditEventDto(
            e.Id, e.OccurredAt, e.EventType, e.EntityType, e.EntityId,
            e.ActorId, e.ActorType, e.CorrelationId, e.Payload.RootElement, e.Metadata.RootElement));
    }
}
