using FluentValidation;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Application.Merchants.DTOs;
using Haworks.Merchant.Domain.Aggregates;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Merchants.Commands.SetOperatingHours;

public record SetOperatingHoursCommand(
    Guid MerchantId,
    Guid UserId,
    List<OperatingHourDto> Hours,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result>;

public class SetOperatingHoursCommandValidator : AbstractValidator<SetOperatingHoursCommand>
{
    public SetOperatingHoursCommandValidator()
    {
        RuleFor(v => v.MerchantId).NotEmpty();
        RuleFor(v => v.Hours).NotNull();
        RuleFor(v => v.Hours).Must(h => h.Count <= 7).WithMessage("Maximum 7 entries allowed (one per day).");
        RuleFor(v => v.Hours).Must(h => h.Select(x => x.Day).Distinct().Count() == h.Count)
            .WithMessage("Duplicate DayOfWeek entries are not allowed.");
        RuleForEach(v => v.Hours).ChildRules(hour =>
        {
            hour.RuleFor(h => h.Day).IsInEnum();
            hour.RuleFor(h => h.Open).LessThan(h => h.Close)
                .When(h => !(h.Open == TimeSpan.Zero && h.Close == TimeSpan.Zero))
                .WithMessage("Open time must be before close time (00:00/00:00 means 24h).");
        });
    }
}

public sealed class SetOperatingHoursCommandHandler : IRequestHandler<SetOperatingHoursCommand, Result>
{
    private readonly IMerchantDbContext _context;

    public SetOperatingHoursCommandHandler(IMerchantDbContext context) => _context = context;

    public async Task<Result> Handle(SetOperatingHoursCommand request, CancellationToken cancellationToken)
    {
        var merchant = await _context.Merchants
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.MerchantId, cancellationToken);

        if (merchant is null)
            return Result.Failure(Error.NotFound("Merchant.NotFound", "Merchant not found."));

        if (merchant.OwnerId != request.UserId)
            return Result.Failure(Error.Forbidden("Merchant.Forbidden", "You are not authorized to update this merchant."));

        var existing = await _context.OperatingHours
            .Where(h => h.MerchantId == request.MerchantId)
            .ToListAsync(cancellationToken);

        _context.OperatingHours.RemoveRange(existing);

        foreach (var dto in request.Hours)
        {
            var hours = OperatingHours.Create(
                request.MerchantId,
                (int)dto.Day,
                dto.Open,
                dto.Close,
                dto.IsOpen);
            _context.OperatingHours.Add(hours);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
