using FluentAssertions;
using Haworks.Payments.Domain;
using Haworks.Contracts.Payments;
using Xunit;

namespace Haworks.Payments.Unit;

/// <summary>
/// Pure-domain Payment invariants. No DB, no MassTransit, no DI.
/// </summary>
public sealed class PaymentTests
{
    private static Payment NewPayment(decimal amount = 100m) =>
        Payment.Create(Guid.NewGuid(), "user-1", amount, tax: 0m, "USD",
            PaymentProvider.Stripe, sagaId: Guid.NewGuid());

    [Fact]
    public void Create_with_zero_amount_is_allowed()
    {
        var p = NewPayment(0m);
        p.Amount.Should().Be(0m);
        p.Status.Should().Be(PaymentStatus.Pending);
        p.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Create_with_negative_amount_throws()
    {
        Action act = () => Payment.Create(Guid.NewGuid(), "u", -1m, 0m, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_negative_tax_throws()
    {
        Action act = () => Payment.Create(Guid.NewGuid(), "u", 1m, -1m, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_userId_throws()
    {
        Action act = () => Payment.Create(Guid.NewGuid(), "", 1m, 0m, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_provider_None_throws()
    {
        Action act = () => Payment.Create(Guid.NewGuid(), "u", 1m, 0m, "USD",
            PaymentProvider.None, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_orderId_throws()
    {
        Action act = () => Payment.Create(Guid.Empty, "u", 1m, 0m, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_sagaId_throws()
    {
        Action act = () => Payment.Create(Guid.NewGuid(), "u", 1m, 0m, "USD",
            PaymentProvider.Stripe, Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AttachProviderSession_transitions_to_Processing()
    {
        var p = NewPayment();
        p.AttachProviderSession("sess_123", "https://checkout.stripe.com/pay/sess_123");
        p.Status.Should().Be(PaymentStatus.Processing);
        p.ProviderSessionId.Should().Be("sess_123");
        p.ProviderCheckoutUrl.Should().Be("https://checkout.stripe.com/pay/sess_123");
    }

    [Fact]
    public void MarkCompleted_sets_IsComplete_and_Status()
    {
        var p = NewPayment();
        p.AttachProviderSession("sess_1", null);
        p.MarkCompleted("pi_xyz", "card");
        p.IsComplete.Should().BeTrue();
        p.Status.Should().Be(PaymentStatus.Completed);
        p.ProviderTransactionId.Should().Be("pi_xyz");
        p.PaymentMethod.Should().Be("card");
    }

    [Fact]
    public void MarkFailed_clears_IsComplete()
    {
        var p = NewPayment();
        p.AttachProviderSession("sess_1", null);
        p.MarkFailed();
        p.IsComplete.Should().BeFalse();
        p.Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public void Flag_transitions_to_Flagged_without_completion()
    {
        var p = NewPayment();
        p.Flag();
        p.Status.Should().Be(PaymentStatus.Flagged);
        p.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void MarkCancelled_transitions_to_Cancelled()
    {
        var p = NewPayment();
        p.MarkCancelled();
        p.Status.Should().Be(PaymentStatus.Cancelled);
    }
}
