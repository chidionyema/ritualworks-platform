using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Application.Ledger.Commands.MatureFunds;

public record MatureFundsCommand() : IRequest;

public class MatureFundsCommandHandler : IRequestHandler<MatureFundsCommand>
{
    private readonly IPayoutsDbContext _context;
    private readonly ILogger<MatureFundsCommandHandler> _logger;

    public MatureFundsCommandHandler(IPayoutsDbContext context, ILogger<MatureFundsCommandHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Handle(MatureFundsCommand request, CancellationToken cancellationToken)
    {
        var pendingAccounts = await _context.LedgerAccounts
            .FromSqlRaw(@"SELECT * FROM payouts.""LedgerAccounts"" WHERE ""Type"" = {0} AND ""Balance"" > 0 ORDER BY ""Id"" ASC FOR UPDATE SKIP LOCKED LIMIT 500", (int)AccountType.SellerPending)
            .ToListAsync(cancellationToken);

        if (pendingAccounts.Count == 0) return;

        var ownerIds = pendingAccounts.Select(a => a.OwnerId).Distinct().ToList();
        var payableAccounts = await _context.LedgerAccounts
            .Where(a => ownerIds.Contains(a.OwnerId) && a.Type == AccountType.SellerPayable)
            .ToDictionaryAsync(a => (a.OwnerId, a.Currency), cancellationToken);

        var maturedCount = 0;
        foreach (var pendingAccount in pendingAccounts)
        {
            var key = (pendingAccount.OwnerId, pendingAccount.Currency);
            if (!payableAccounts.TryGetValue(key, out var payableAccount))
            {
                payableAccount = LedgerAccount.Create(pendingAccount.OwnerId, AccountType.SellerPayable, pendingAccount.Currency);
                payableAccounts[key] = payableAccount;
                _context.LedgerAccounts.Add(payableAccount);
            }

            var amount = pendingAccount.Balance;
            pendingAccount.UpdateBalance(amount, EntryType.Debit);
            payableAccount.UpdateBalance(amount, EntryType.Credit);

            var transactionId = Guid.NewGuid();
            _context.LedgerEntries.AddRange(
                LedgerEntry.Create(pendingAccount.Id, transactionId, amount, EntryType.Debit, "Funds matured", "MATURITY"),
                LedgerEntry.Create(payableAccount.Id, transactionId, amount, EntryType.Credit, "Funds matured", "MATURITY"));
            maturedCount++;
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Matured funds for {Count} accounts", maturedCount);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Another instance already matured some of these accounts — safe to skip
            _logger.LogWarning(ex, "Concurrency conflict during fund maturity — another instance handled some accounts");
        }
    }
}
