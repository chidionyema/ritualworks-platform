using Haworks.Payouts.Application.Ledger.Queries.GetBalance;
using Haworks.Payouts.Domain.Enums;
using Haworks.BuildingBlocks.Extensions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LedgerController(IMediator mediator) : ControllerBase
{
    [HttpGet("balance/{ownerId}")]
    public async Task<IActionResult> GetBalance(Guid ownerId, [FromQuery] AccountType type, [FromQuery] string currency = "USD")
    {
        var userId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // Sellers can only view their own balances
        if (!Guid.TryParse(userId, out var parsedUserId) || parsedUserId != ownerId)
            return Forbid();

        // M1 Fix: Non-admin callers can only query seller account types (not platform internals)
        if (!User.IsInRole("Admin") &&
            type is not (AccountType.SellerPending or AccountType.SellerPayable))
        {
            return Forbid();
        }

        var balance = await mediator.Send(new GetBalanceQuery(ownerId, type, currency));
        return Ok(new { Balance = balance, Currency = currency });
    }
}
