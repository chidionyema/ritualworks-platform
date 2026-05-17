using FluentValidation.TestHelper;
using Haworks.Location.Application.Queries;
using Xunit;

namespace Haworks.Location.Unit.Queries;

public class GetNearbyAddressesQueryValidatorTests
{
    private readonly GetNearbyAddressesQueryValidator _validator = new();

    [Fact]
    public void Validate_WithDefaultLimit_ShouldNotHaveError()
    {
        var query = new GetNearbyAddressesQuery(51.5, -0.1);
        var result = _validator.TestValidate(query);
        result.ShouldNotHaveValidationErrorFor(x => x.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WithInvalidLimit_ShouldHaveError(int limit)
    {
        var query = new GetNearbyAddressesQuery(51.5, -0.1, Limit: limit);
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Limit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void Validate_WithValidLimit_ShouldNotHaveError(int limit)
    {
        var query = new GetNearbyAddressesQuery(51.5, -0.1, Limit: limit);
        var result = _validator.TestValidate(query);
        result.ShouldNotHaveValidationErrorFor(x => x.Limit);
    }
}
