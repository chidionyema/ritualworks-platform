using FluentValidation;

namespace Haworks.Privacy.Application.Requests.Queries.GetErasureStatus;

public class GetErasureStatusQueryValidator : AbstractValidator<GetErasureStatusQuery>
{
    public GetErasureStatusQueryValidator()
    {
        RuleFor(x => x.RequestId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserId).NotEqual(Guid.Empty);
    }
}
