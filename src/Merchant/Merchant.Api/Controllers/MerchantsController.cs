using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;
using Haworks.Merchant.Application.Merchants.Commands.ApproveMerchant;
using Haworks.Merchant.Application.Merchants.Commands.CreateMerchant;
using Haworks.Merchant.Application.Merchants.Commands.DeactivateMerchant;
using Haworks.Merchant.Application.Merchants.Commands.RejectMerchant;
using Haworks.Merchant.Application.Merchants.Commands.SetOperatingHours;
using Haworks.Merchant.Application.Merchants.Commands.SuspendMerchant;
using Haworks.Merchant.Application.Merchants.Commands.UpdateMerchant;
using Haworks.Merchant.Application.Merchants.DTOs;
using Haworks.Merchant.Application.Merchants.Queries;
using Haworks.Merchant.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Merchant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MerchantsController : ControllerBase
{
    private readonly IMediator _mediator;

    public MerchantsController(IMediator mediator) => _mediator = mediator;

    [HttpPost]
    public async Task<IActionResult> Create(CreateMerchantCommand command, CancellationToken ct)
    {
        var id = await _mediator.Send(command, ct);
        return Ok(new { MerchantId = id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetMerchantByIdQuery(id), ct);
        return result.ToActionResult();
    }

    [HttpGet("by-slug/{slug}")]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetMerchantBySlugQuery(slug), ct);
        return result.ToActionResult();
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var ownerGuid))
            return Unauthorized();

        var result = await _mediator.Send(new GetMerchantByOwnerQuery(ownerGuid), ct);
        return result.ToActionResult();
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        [FromQuery] MerchantStatus? status = null,
        [FromQuery] bool includeDeactivated = false,
        CancellationToken ct = default)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 100);
        var result = await _mediator.Send(new ListMerchantsQuery(skip, take, status, includeDeactivated), ct);
        return result.ToActionResult();
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateMerchantRequest request, CancellationToken ct)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var userGuid))
            return Unauthorized();

        var command = new UpdateMerchantCommand(
            id, userGuid, request.Name, request.Bio, request.LogoUrl,
            request.Description, request.ContactEmail, request.ContactPhone,
            request.Category, request.Website);

        var result = await _mediator.Send(command, ct);
        return result.ToNoContentActionResult();
    }

    [HttpPut("{id:guid}/hours")]
    public async Task<IActionResult> SetHours(Guid id, [FromBody] List<OperatingHourDto> hours, CancellationToken ct)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var userGuid))
            return Unauthorized();

        var result = await _mediator.Send(new SetOperatingHoursCommand(id, userGuid, hours), ct);
        return result.ToNoContentActionResult();
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var adminId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(adminId))
            return Unauthorized();

        var result = await _mediator.Send(new ApproveMerchantCommand(id, adminId), ct);
        return result.ToNoContentActionResult();
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectMerchantRequest request, CancellationToken ct)
    {
        var adminId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(adminId))
            return Unauthorized();

        var result = await _mediator.Send(new RejectMerchantCommand(id, adminId, request.Reason), ct);
        return result.ToNoContentActionResult();
    }

    [HttpPost("{id:guid}/suspend")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Suspend(Guid id, [FromBody] SuspendMerchantRequest request, CancellationToken ct)
    {
        var adminId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(adminId))
            return Unauthorized();

        var result = await _mediator.Send(new SuspendMerchantCommand(id, adminId, request.Reason), ct);
        return result.ToNoContentActionResult();
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var userGuid))
            return Unauthorized();

        var result = await _mediator.Send(new DeactivateMerchantCommand(id, userGuid), ct);
        return result.ToNoContentActionResult();
    }
}

public sealed record UpdateMerchantRequest(
    string? Name,
    string? Bio,
    string? LogoUrl,
    string? Description,
    string? ContactEmail,
    string? ContactPhone,
    string? Category,
    string? Website);

public sealed record RejectMerchantRequest(string Reason);

public sealed record SuspendMerchantRequest(string Reason);
