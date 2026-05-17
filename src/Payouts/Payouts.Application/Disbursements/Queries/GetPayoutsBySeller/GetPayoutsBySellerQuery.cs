using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Disbursements.Queries.GetPayoutsBySeller;

public record GetPayoutsBySellerQuery(Guid SellerId, int Skip = 0, int Take = 20) : IRequest<List<Payout>>;

public class GetPayoutsBySellerQueryHandler : IRequestHandler<GetPayoutsBySellerQuery, List<Payout>>
{
    private readonly IPayoutsDbContext _context;
    public GetPayoutsBySellerQueryHandler(IPayoutsDbContext context) { _context = context; }

    public Task<List<Payout>> Handle(GetPayoutsBySellerQuery request, CancellationToken cancellationToken)
    {
        var skip = Math.Max(0, request.Skip);
        var take = Math.Clamp(request.Take, 1, 100);

        return _context.Payouts
            .Where(p => p.SellerId == request.SellerId)
            .OrderByDescending(p => p.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
