using System.Security.Claims;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;
using Haworks.Catalog.Application.Commands.Reservations;
using Haworks.Catalog.Application.DTOs.Reservations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Catalog.Api.Controllers;

/// <summary>
/// ADR-004 phase 4 sync reservation flow:
///   POST /api/checkout/reservations              — create + 15-min hold (201/409)
///   POST /api/checkout/reservations/{id}/confirm — bind to a server-issued OrderId (200/404/410/409)
///
/// The route is under <c>/api/checkout/</c> rather than <c>/api/reservations/</c>
/// to match the monolith's URL exactly so existing frontends keep working.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/checkout/reservations")]
public sealed class ReservationsController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Stand-in user id for anonymous create-reservation calls. ADR-004 lets
    /// the create step run without authentication so a "hold inventory while
    /// I enter payment details" flow works for guest checkouts; the confirm
    /// step (below) requires <see cref="AuthorizeAttribute"/>.
    /// </summary>
    private const string GuestUserId = "guest";

    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ReservationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateReservationRequest body,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        // BFF-forwarded user id (A1/A4). Anonymous callers get the guest
        // constant; the per-reservation row still has a non-empty UserId
        // so the sweeper's "release on expiry" path doesn't have to special-case.
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) userId = GuestUserId;

        var clientKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey;

        var items = (body.Items ?? Array.Empty<ReservationItemDto>())
            .Select(i => new ReservationItemDto(i.ProductId, i.ProductName ?? string.Empty, i.Quantity))
            .ToList();

        var result = await mediator.Send(
            new CreateReservationCommand(items, userId, clientKey),
            ct);

        if (result.IsFailure) return result.ToActionResult();

        // 201 with the resource id; the absolute URI is irrelevant here
        // because B2 deliberately doesn't expose a GET-by-id read endpoint
        // (out of scope per the brief).
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpPost("{reservationId:guid}/confirm")]
    [Authorize]
    [ProducesResponseType(typeof(ConfirmReservationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> Confirm(
        Guid reservationId,
        [FromBody] ConfirmReservationRequest body,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // ADR-004 phase 4 mandates an email claim for confirm — the
        // downstream payment session needs a customer-email to mint a
        // gateway session. Surface 400 if missing rather than guessing.
        var email = User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(email))
        {
            return BadRequest(new { error = "Reservation.MissingEmail", message = "Email claim required." });
        }

        var clientKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey;

        var result = await mediator.Send(
            new ConfirmReservationCommand(
                reservationId,
                userId,
                email,
                body.TotalAmount,
                body.Currency,
                clientKey),
            ct);

        if (result.IsFailure)
        {
            // Reservation.Expired needs 410 specifically; the generic Result
            // mapping returns 409 for any Conflict-typed error.
            return result.Error.Code switch
            {
                "Reservation.Expired" => StatusCode(StatusCodes.Status410Gone, new { error = result.Error.Message }),
                _ => result.ToActionResult(),
            };
        }

        return Ok(result.Value);
    }
}

/// <summary>Body for <c>POST /api/checkout/reservations</c>.</summary>
public sealed record CreateReservationRequest(
    IReadOnlyList<ReservationItemDto>? Items);

/// <summary>Body for <c>POST /api/checkout/reservations/{id}/confirm</c>.</summary>
public sealed record ConfirmReservationRequest
{
    public required decimal TotalAmount { get; init; }
    public required string Currency { get; init; }
}
