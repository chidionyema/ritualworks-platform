using FluentValidation;

namespace Haworks.Location.Application.Queries;

public class GetNearbyAddressesQueryValidator : AbstractValidator<GetNearbyAddressesQuery>
{
    public GetNearbyAddressesQueryValidator()
    {
        RuleFor(x => x.Lat).InclusiveBetween(-90, 90);
        RuleFor(x => x.Lon).InclusiveBetween(-180, 180);
        RuleFor(x => x.RadiusMeters).InclusiveBetween(1, 50000);
    }
}
