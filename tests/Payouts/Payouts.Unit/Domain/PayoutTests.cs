using FluentAssertions;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Xunit;

namespace Haworks.Payouts.Unit.Domain;

public class PayoutTests
{
    [Fact]
    public void Create_with_valid_amount_succeeds()
    {
        var payout = Payout.Create(Guid.NewGuid(), 100m, "USD");
        payout.Status.Should().Be(PayoutStatus.Pending);
        payout.Amount.Should().Be(100m);
    }

    [Fact]
    public void Create_with_zero_amount_throws()
    {
        var act = () => Payout.Create(Guid.NewGuid(), 0m, "USD");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_negative_amount_throws()
    {
        var act = () => Payout.Create(Guid.NewGuid(), -10m, "USD");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkInTransit_sets_status_and_reference()
    {
        var payout = Payout.Create(Guid.NewGuid(), 50m, "USD");
        payout.MarkInTransit("tr_abc");
        payout.Status.Should().Be(PayoutStatus.InTransit);
        payout.ExternalReference.Should().Be("tr_abc");
    }

    [Fact]
    public void MarkSucceeded_sets_status_and_timestamp()
    {
        var payout = Payout.Create(Guid.NewGuid(), 50m, "USD");
        payout.MarkSucceeded();
        payout.Status.Should().Be(PayoutStatus.Succeeded);
        payout.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_sets_reason()
    {
        var payout = Payout.Create(Guid.NewGuid(), 50m, "USD");
        payout.MarkFailed("Insufficient funds in connected account");
        payout.Status.Should().Be(PayoutStatus.Failed);
        payout.FailureReason.Should().Contain("Insufficient");
    }
}
