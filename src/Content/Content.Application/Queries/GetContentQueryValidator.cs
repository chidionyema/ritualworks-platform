using FluentValidation;

namespace Haworks.Content.Application.Queries;

public class GetContentQueryValidator : AbstractValidator<GetContentQuery>
{
    public GetContentQueryValidator()
    {
        RuleFor(x => x.ContentId).NotEmpty();
    }
}
