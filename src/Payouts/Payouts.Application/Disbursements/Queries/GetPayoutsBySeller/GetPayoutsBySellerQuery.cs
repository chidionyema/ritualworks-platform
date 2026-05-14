using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Disbursements.Queries.GetPayoutsBySeller;

public record GetPayoutsBySellerQuery(Guid SellerId) : IRequest<List<Payout>>;

public class GetPayoutsBySellerQueryHandler : IRequestHandler<GetPayoutsBySellerQuery, List<Payout>>
{
    private readonly IPayoutsDbContext _context;
    public GetPayoutsBySellerQueryHandler(IPayoutsDbContext context) { _context = context; }
    public async Task<List<Payout>> Handle(GetPayoutsBySellerQuery request, CancellationToken cancellationToken) => await _context.Payouts.Where(p => p.SellerId == request.SellerId).OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
}
