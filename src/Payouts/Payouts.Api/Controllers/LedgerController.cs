using Haworks.Payouts.Application.Ledger.Queries.GetBalance;
using Haworks.Payouts.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Payouts.Api.Controllers;

[ApiController][Route("api/[controller]")]
[Authorize]
public class LedgerController : ControllerBase
{
    private readonly IMediator _mediator;
    public LedgerController(IMediator mediator) { _mediator = mediator; }
    [HttpGet("balance/{ownerId}")] public async Task<IActionResult> GetBalance(Guid ownerId, [FromQuery] AccountType type, [FromQuery] string currency = "USD") => Ok(new { Balance = await _mediator.Send(new GetBalanceQuery(ownerId, type, currency)), Currency = currency });
}
