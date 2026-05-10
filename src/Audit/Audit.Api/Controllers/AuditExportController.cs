using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Haworks.Audit.Application.Export;

namespace Haworks.Audit.Api.Controllers;

[ApiController]
[Route("audit/export")]
[Authorize]
public class AuditExportController : ControllerBase
{
    private readonly IAuditExportJob _exportService;

    public AuditExportController(IAuditExportJob exportService)
    {
        _exportService = exportService;
    }

    [HttpPost]
    [Authorize(Roles = "audit-admin")]
    public async Task<IActionResult> EnqueueExport([FromBody] AuditExportRequest request, CancellationToken ct)
    {
        var requestedBy = User.Identity?.Name ?? "unknown";
        var jobId = await _exportService.EnqueueAsync(request, requestedBy, ct);
        return Accepted(new { jobId, status = "queued" });
    }

    [HttpGet("{jobId:guid}")]
    [Authorize(Roles = "audit-reader")]
    public async Task<IActionResult> GetExportStatus(Guid jobId, CancellationToken ct)
    {
        var snapshot = await _exportService.GetStatusAsync(jobId, ct);
        if (snapshot == null) return NotFound();
        return Ok(snapshot);
    }
}
