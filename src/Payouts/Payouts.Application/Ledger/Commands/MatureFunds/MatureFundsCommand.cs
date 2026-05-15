using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Ledger.Commands.MatureFunds;

public record MatureFundsCommand() : IRequest;

public class MatureFundsCommandHandler : IRequestHandler<MatureFundsCommand>
{
    private readonly IPayoutsDbContext _context;
    public MatureFundsCommandHandler(IPayoutsDbContext context) { _context = context; }
    public async Task Handle(MatureFundsCommand request, CancellationToken cancellationToken)
    {
        var pendingAccounts = await _context.LedgerAccounts.Where(a => a.Type == AccountType.SellerPending && a.Balance > 0).Take(500).ToListAsync(cancellationToken);
        var ownerIds = pendingAccounts.Select(a => a.OwnerId).ToList();
        var payableAccounts = await _context.LedgerAccounts
            .Where(a => ownerIds.Contains(a.OwnerId) && a.Type == AccountType.SellerPayable)
            .ToDictionaryAsync(a => (a.OwnerId, a.Currency), cancellationToken);
        foreach (var pendingAccount in pendingAccounts)
        {
            var key = (pendingAccount.OwnerId, pendingAccount.Currency);
            if (!payableAccounts.TryGetValue(key, out var payableAccount))
            {
                payableAccount = LedgerAccount.Create(pendingAccount.OwnerId, AccountType.SellerPayable, pendingAccount.Currency);
                payableAccounts[key] = payableAccount;
            }
            if (payableAccount.Id == Guid.Empty || !_context.LedgerAccounts.Local.Contains(payableAccount)) _context.LedgerAccounts.Add(payableAccount);
            var amount = pendingAccount.Balance;
            pendingAccount.UpdateBalance(amount, EntryType.Debit);
            payableAccount.UpdateBalance(amount, EntryType.Credit);
            var transactionId = Guid.NewGuid();
            _context.LedgerEntries.AddRange(LedgerEntry.Create(pendingAccount.Id, transactionId, amount, EntryType.Debit, "Funds matured", "MATURITY"), LedgerEntry.Create(payableAccount.Id, transactionId, amount, EntryType.Credit, "Funds matured", "MATURITY"));
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}
