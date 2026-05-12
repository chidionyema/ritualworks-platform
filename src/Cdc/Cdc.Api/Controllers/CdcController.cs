using Haworks.Cdc.Application;
using Haworks.Cdc.Application.Interfaces;
using Haworks.Cdc.Domain.Aggregates;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Cdc.Api.Controllers;

[ApiController]
[Route("api/cdc")]
public sealed class CdcController : ControllerBase
{
    private readonly ICdcStore _store;
    private readonly CdcRelayManager _relayManager;

    public CdcController(ICdcStore store, CdcRelayManager relayManager)
    {
        _store = store;
        _relayManager = relayManager;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        // For status, we might need all sources, not just enabled ones
        // Assuming ICdcStore might need an update or we use a more generic query if it was DB
        // But for T2, let's just stick to enabled for now or add a method.
        
        var sources = await _store.GetEnabledSourcesAsync(ct);
        var active = _relayManager.GetActiveSources();

        return Ok(sources.Select(s => new
        {
            s.ServiceName,
            s.Enabled,
            IsRunning = active.Contains(s.ServiceName),
            s.SlotName,
            s.PublicationName
        }));
    }

    [HttpPost("sources")]
    public async Task<IActionResult> AddSource([FromBody] CdcSourceDto request, CancellationToken ct)
    {
        var source = CdcSource.Create(request.ServiceName, request.ConnectionString, request.SlotName);
        await _store.AddSourceAsync(source, ct);
        await _store.SaveChangesAsync(ct);
        
        return Created($"/api/cdc/sources/{source.ServiceName}", source);
    }

    [HttpPost("sources/{name}/pause")]
    public async Task<IActionResult> PauseSource(string name, CancellationToken ct)
    {
        var source = await _store.GetSourceByNameAsync(name, ct);
        if (source == null) return NotFound();

        source.Enabled = false;
        await _store.SaveChangesAsync(ct);
        
        return Ok();
    }

    [HttpPost("sources/{name}/resume")]
    public async Task<IActionResult> ResumeSource(string name, CancellationToken ct)
    {
        var source = await _store.GetSourceByNameAsync(name, ct);
        if (source == null) return NotFound();

        source.Enabled = true;
        await _store.SaveChangesAsync(ct);
        
        return Ok();
    }
}

public sealed record CdcSourceDto(string ServiceName, string ConnectionString, string SlotName);
