using FluentAssertions;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Xunit;

namespace Haworks.Payouts.Unit.Domain;

public class PayoutTests
{
    private static Payout CreatePending() => Payout.Create(Guid.NewGuid(), 100m, "USD");

    private static Payout CreateInTransit()
    {
        var p = CreatePending();
        p.MarkInTransit("tr_123");
        return p;
    }

    // --- Create validation ---

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

    [Theory]
    [InlineData("")]
    [InlineData("us")]
    [InlineData("USDD")]
    [InlineData("usd")]
    [InlineData("U1D")]
    public void Create_with_invalid_currency_throws(string currency)
    {
        var act = () => Payout.Create(Guid.NewGuid(), 50m, currency);
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("currency");
    }

    // --- Happy-path transitions ---

    [Fact]
    public void MarkInTransit_from_Pending_sets_status_and_reference()
    {
        var payout = CreatePending();
        payout.MarkInTransit("tr_abc", "transit_ref_1");
        payout.Status.Should().Be(PayoutStatus.InTransit);
        payout.ExternalReference.Should().Be("tr_abc");
        payout.TransitReference.Should().Be("transit_ref_1");
    }

    [Fact]
    public void MarkSucceeded_from_InTransit_sets_status_and_timestamp()
    {
        var payout = CreateInTransit();
        payout.MarkSucceeded();
        payout.Status.Should().Be(PayoutStatus.Succeeded);
        payout.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_from_InTransit_sets_reason_and_timestamp()
    {
        var payout = CreateInTransit();
        payout.MarkFailed("Insufficient funds in connected account");
        payout.Status.Should().Be(PayoutStatus.Failed);
        payout.FailureReason.Should().Contain("Insufficient");
        payout.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkCancelled_from_Pending_sets_status()
    {
        var payout = CreatePending();
        payout.MarkCancelled();
        payout.Status.Should().Be(PayoutStatus.Cancelled);
        payout.ProcessedAt.Should().NotBeNull();
    }

    // --- Invalid transitions ---

    [Fact]
    public void MarkInTransit_from_Succeeded_throws()
    {
        var payout = CreateInTransit();
        payout.MarkSucceeded();
        var act = () => payout.MarkInTransit("ref");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkInTransit_from_Failed_throws()
    {
        var payout = CreateInTransit();
        payout.MarkFailed("err");
        var act = () => payout.MarkInTransit("ref");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkInTransit_from_Cancelled_throws()
    {
        var payout = CreatePending();
        payout.MarkCancelled();
        var act = () => payout.MarkInTransit("ref");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSucceeded_from_Pending_throws()
    {
        var payout = CreatePending();
        var act = () => payout.MarkSucceeded();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSucceeded_from_Succeeded_throws()
    {
        var payout = CreateInTransit();
        payout.MarkSucceeded();
        var act = () => payout.MarkSucceeded();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_from_Pending_succeeds()
    {
        var payout = CreatePending();
        payout.MarkFailed("gateway error");
        payout.Status.Should().Be(PayoutStatus.Failed);
        payout.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkFailed_from_Succeeded_throws()
    {
        var payout = CreateInTransit();
        payout.MarkSucceeded();
        var act = () => payout.MarkFailed("reason");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_from_Failed_throws()
    {
        var payout = CreateInTransit();
        payout.MarkFailed("first");
        var act = () => payout.MarkFailed("second");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_from_Cancelled_throws()
    {
        var payout = CreatePending();
        payout.MarkCancelled();
        var act = () => payout.MarkFailed("reason");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkCancelled_from_InTransit_throws()
    {
        var payout = CreateInTransit();
        var act = () => payout.MarkCancelled();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkCancelled_from_Succeeded_throws()
    {
        var payout = CreateInTransit();
        payout.MarkSucceeded();
        var act = () => payout.MarkCancelled();
        act.Should().Throw<InvalidOperationException>();
    }
}
