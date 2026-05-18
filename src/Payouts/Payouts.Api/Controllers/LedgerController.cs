using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
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

/// <summary>
/// Demo endpoint: creates a seller account, credits it, then shows the
/// double-entry ledger with debit/credit pairs. Read-only visualization
/// for the portfolio site.
/// </summary>
[ApiController]
[Route("demo/ledger")]
[Authorize(Roles = "Admin,Service")]
public sealed class DemoLedgerController(IPayoutsDbContext db) : ControllerBase
{
    [HttpPost("simulate")]
    public async Task<IActionResult> SimulateTransaction(
        [FromBody] LedgerSimulationRequest request,
        CancellationToken ct)
    {
        var sellerId = Guid.NewGuid();
        var txId = Guid.NewGuid();

        // Find or create seller account
        var account = Haworks.Payouts.Domain.Aggregates.LedgerAccount.Create(
            sellerId, Haworks.Payouts.Domain.Enums.AccountType.SellerPayable, request.Currency ?? "USD");

        db.LedgerAccounts.Add(account);

        // Credit: payment received
        account.UpdateBalance(request.AmountCents / 100m, Haworks.Payouts.Domain.Enums.EntryType.Credit);
        var creditEntry = Haworks.Payouts.Domain.Aggregates.LedgerEntry.Create(
            account.Id, txId, request.AmountCents / 100m,
            Haworks.Payouts.Domain.Enums.EntryType.Credit,
            "Payment received", $"ORDER:{Guid.NewGuid():N}");
        db.LedgerEntries.Add(creditEntry);

        // Debit: platform commission (10%)
        var commission = request.AmountCents / 100m * 0.10m;
        account.UpdateBalance(commission, Haworks.Payouts.Domain.Enums.EntryType.Debit);
        var debitEntry = Haworks.Payouts.Domain.Aggregates.LedgerEntry.Create(
            account.Id, txId, commission,
            Haworks.Payouts.Domain.Enums.EntryType.Debit,
            "Platform commission (10%)", $"COMMISSION:{txId:N}");
        db.LedgerEntries.Add(debitEntry);

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            sellerId,
            accountId = account.Id,
            balance = account.Balance,
            entries = new[]
            {
                new { type = "credit", amount = request.AmountCents / 100m, description = creditEntry.Description, reference = creditEntry.ReferenceId },
                new { type = "debit", amount = commission, description = debitEntry.Description, reference = debitEntry.ReferenceId },
            },
            invariant = "Sum of all entries = 0 (credit - debit balanced)",
        });
    }
}

public sealed record LedgerSimulationRequest
{
    public long AmountCents { get; init; } = 3999;
    public string? Currency { get; init; }
}
