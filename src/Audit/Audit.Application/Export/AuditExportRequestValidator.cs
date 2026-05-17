using FluentValidation;

namespace Haworks.Audit.Application.Export;

public sealed class AuditExportRequestValidator : AbstractValidator<AuditExportRequest>
{
    public const int MaxDateRangeDays = 90;
    public const int MaxLimit = 10_000;

    public AuditExportRequestValidator()
    {
        RuleFor(x => x.From)
            .NotEmpty()
            .WithMessage("Start date is required.");

        RuleFor(x => x.To)
            .NotEmpty()
            .WithMessage("End date is required.");

        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithMessage("End date must be after start date.");

        RuleFor(x => x)
            .Must(x => (x.To - x.From).TotalDays <= MaxDateRangeDays)
            .WithMessage($"Date range must not exceed {MaxDateRangeDays} days.");
    }
}
