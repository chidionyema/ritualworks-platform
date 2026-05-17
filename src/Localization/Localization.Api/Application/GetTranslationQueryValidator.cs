using FluentValidation;

namespace Haworks.Localization.Api.Application;

public class GetTranslationQueryValidator : AbstractValidator<GetTranslationQuery>
{
    private const string LocalePattern = @"^[a-z]{2,3}(-[A-Za-z]{2,8})?$";

    public GetTranslationQueryValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.Locale)
            .NotEmpty()
            .Matches(LocalePattern)
            .WithMessage("Locale must match a valid format (e.g. 'en', 'en-US', 'fr-FR').");
    }
}
