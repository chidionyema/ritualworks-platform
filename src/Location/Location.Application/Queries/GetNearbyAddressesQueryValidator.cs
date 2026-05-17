using FluentValidation;

namespace Haworks.Location.Application.Queries;

public sealed class GetNearbyAddressesQueryValidator : AbstractValidator<GetNearbyAddressesQuery>
{
    public GetNearbyAddressesQueryValidator()
    {
        RuleFor(x => x.Lat).InclusiveBetween(-90, 90);
        RuleFor(x => x.Lon).InclusiveBetween(-180, 180);
        RuleFor(x => x.RadiusMeters).GreaterThan(0).LessThanOrEqualTo(50000);
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
    }
}
