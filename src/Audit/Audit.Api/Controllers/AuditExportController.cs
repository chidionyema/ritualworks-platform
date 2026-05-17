using FluentValidation;
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
    private readonly IValidator<AuditExportRequest> _validator;

    public AuditExportController(IAuditExportJob exportService, IValidator<AuditExportRequest> validator)
    {
        _exportService = exportService;
        _validator = validator;
    }

    [HttpPost]
    [Authorize(Roles = "audit-admin")]
    public async Task<IActionResult> EnqueueExport([FromBody] AuditExportRequest request, CancellationToken ct)
    {
        var validationResult = await _validator.ValidateAsync(request, ct);
        if (!validationResult.IsValid)
            return BadRequest(validationResult.Errors.Select(e => new { e.PropertyName, e.ErrorMessage }));

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
