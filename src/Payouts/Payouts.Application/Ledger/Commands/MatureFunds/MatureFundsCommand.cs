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
        var pendingAccounts = await _context.LedgerAccounts.Where(a => a.Type == AccountType.SellerPending && a.Balance > 0).ToListAsync(cancellationToken);
        foreach (var pendingAccount in pendingAccounts)
        {
            var payableAccount = await _context.LedgerAccounts.FirstOrDefaultAsync(a => a.OwnerId == pendingAccount.OwnerId && a.Type == AccountType.SellerPayable && a.Currency == pendingAccount.Currency, cancellationToken) ?? LedgerAccount.Create(pendingAccount.OwnerId, AccountType.SellerPayable, pendingAccount.Currency);
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
