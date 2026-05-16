using Haworks.BuildingBlocks.Common;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Application.Merchants.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Queries;

public record GetMerchantBySlugQuery(string Slug) : IRequest<Result<MerchantDto>>;

public sealed class GetMerchantBySlugQueryHandler : IRequestHandler<GetMerchantBySlugQuery, Result<MerchantDto>>
{
    private readonly IMerchantDbContext _context;

    public GetMerchantBySlugQueryHandler(IMerchantDbContext context) => _context = context;

    public async Task<Result<MerchantDto>> Handle(GetMerchantBySlugQuery request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Slug == request.Slug, cancellationToken);

        if (merchant is null)
            return Result.Failure<MerchantDto>(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        var hours = await _context.OperatingHours
            .AsNoTracking()
            .Where(h => h.MerchantId == merchant.Id)
            .Select(h => new OperatingHourDto((DayOfWeek)h.DayOfWeek, h.OpenTime, h.CloseTime, h.IsOpen))
            .ToListAsync(cancellationToken);

        return Result.Success(new MerchantDto(
            merchant.Id, merchant.OwnerId, merchant.Name, merchant.Slug, merchant.Bio,
            merchant.LogoUrl, merchant.Description, merchant.ContactEmail, merchant.ContactPhone,
            merchant.Category, merchant.Website, merchant.Status, hours));
    }
}
