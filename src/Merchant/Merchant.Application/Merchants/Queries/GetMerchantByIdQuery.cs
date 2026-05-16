using Haworks.BuildingBlocks.Common;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Application.Merchants.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Queries;

public record GetMerchantByIdQuery(Guid MerchantId) : IRequest<Result<MerchantDto>>;

public sealed class GetMerchantByIdQueryHandler : IRequestHandler<GetMerchantByIdQuery, Result<MerchantDto>>
{
    private readonly IMerchantDbContext _context;

    public GetMerchantByIdQueryHandler(IMerchantDbContext context) => _context = context;

    public async Task<Result<MerchantDto>> Handle(GetMerchantByIdQuery request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.MerchantId, cancellationToken);

        if (merchant is null)
            return Result.Failure<MerchantDto>(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        var hours = await _context.OperatingHours
            .AsNoTracking()
            .Where(h => h.MerchantId == merchant.Id)
            .Select(h => new OperatingHourDto((DayOfWeek)h.DayOfWeek, h.OpenTime, h.CloseTime, h.IsOpen))
            .ToListAsync(cancellationToken);

        return Result.Success(MapToDto(merchant, hours));
    }

    private static MerchantDto MapToDto(Domain.Aggregates.MerchantProfile m, IReadOnlyList<OperatingHourDto> hours) =>
        new(m.Id, m.OwnerId, m.Name, m.Slug, m.Bio, m.LogoUrl, m.Description,
            m.ContactEmail, m.ContactPhone, m.Category, m.Website, m.Status, hours);
}
