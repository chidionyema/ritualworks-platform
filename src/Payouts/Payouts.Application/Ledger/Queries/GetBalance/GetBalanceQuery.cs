using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Ledger.Queries.GetBalance;

public record GetBalanceQuery(Guid OwnerId, AccountType Type, string Currency) : IRequest<decimal>;

public class GetBalanceQueryHandler : IRequestHandler<GetBalanceQuery, decimal>
{
    private readonly IPayoutsDbContext _context;
    public GetBalanceQueryHandler(IPayoutsDbContext context) { _context = context; }
    public async Task<decimal> Handle(GetBalanceQuery request, CancellationToken cancellationToken)
    {
        var account = await _context.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.OwnerId == request.OwnerId && a.Type == request.Type && a.Currency == request.Currency, cancellationToken);
        return account?.Balance ?? 0;
    }
}
