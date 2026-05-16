using FluentAssertions;
using Haworks.Merchant.Domain.Aggregates;
using Xunit;

namespace Haworks.Merchant.Unit.Domain;

public sealed class OperatingHoursTests
{
    [Fact]
    public void Create_sets_IsOpen_to_true_by_default()
    {
        var hours = OperatingHours.Create(Guid.NewGuid(), 1, TimeSpan.FromHours(9), TimeSpan.FromHours(17));

        hours.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Close_sets_IsOpen_to_false()
    {
        var hours = OperatingHours.Create(Guid.NewGuid(), 1, TimeSpan.FromHours(9), TimeSpan.FromHours(17));

        hours.Close();

        hours.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Open_sets_IsOpen_to_true()
    {
        var hours = OperatingHours.Create(Guid.NewGuid(), 1, TimeSpan.FromHours(9), TimeSpan.FromHours(17), false);

        hours.Open();

        hours.IsOpen.Should().BeTrue();
    }
}
