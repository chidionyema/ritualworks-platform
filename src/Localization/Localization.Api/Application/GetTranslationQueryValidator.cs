using FluentValidation;

namespace Haworks.Localization.Api.Application;

public class GetTranslationQueryValidator : AbstractValidator<GetTranslationQuery>
{
    public GetTranslationQueryValidator()
    {
        RuleFor(x => x.Key).NotEmpty();
        RuleFor(x => x.Locale).NotEmpty();
    }
}
