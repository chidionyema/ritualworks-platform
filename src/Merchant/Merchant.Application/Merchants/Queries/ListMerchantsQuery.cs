using Haworks.BuildingBlocks.Common;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Application.Merchants.DTOs;
using Haworks.Merchant.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Queries;

public record ListMerchantsQuery(int Skip, int Take, MerchantStatus? Status, bool IncludeDeactivated = false) : IRequest<Result<PagedResult<MerchantDto>>>;

public sealed class ListMerchantsQueryHandler : IRequestHandler<ListMerchantsQuery, Result<PagedResult<MerchantDto>>>
{
    private readonly IMerchantDbContext _context;

    public ListMerchantsQueryHandler(IMerchantDbContext context) => _context = context;

    public async Task<Result<PagedResult<MerchantDto>>> Handle(ListMerchantsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Merchants.AsNoTracking().AsQueryable();

        if (request.Status.HasValue)
            query = query.Where(m => m.Status == request.Status.Value);
        else if (!request.IncludeDeactivated)
            query = query.Where(m => m.Status != MerchantStatus.Deactivated);

        var total = await query.CountAsync(cancellationToken);

        var merchants = await query
            .OrderBy(m => m.Name)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToListAsync(cancellationToken);

        var merchantIds = merchants.Select(m => m.Id).ToList();
        var allHours = await _context.OperatingHours
            .AsNoTracking()
            .Where(h => merchantIds.Contains(h.MerchantId))
            .ToListAsync(cancellationToken);

        var hoursByMerchant = allHours
            .GroupBy(h => h.MerchantId)
            .ToDictionary(g => g.Key, g => g.Select(h => new OperatingHourDto((DayOfWeek)h.DayOfWeek, h.OpenTime, h.CloseTime, h.IsOpen)).ToList() as IReadOnlyList<OperatingHourDto>);

        var items = merchants.Select(m => new MerchantDto(
            m.Id, m.OwnerId, m.Name, m.Slug, m.Bio, m.LogoUrl, m.Description,
            m.ContactEmail, m.ContactPhone, m.Category, m.Website, m.Status,
            hoursByMerchant.GetValueOrDefault(m.Id, []))).ToList();

        return Result.Success(new PagedResult<MerchantDto>(items, total, request.Skip, request.Take));
    }
}
