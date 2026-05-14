using FluentAssertions;
using Haworks.Payments.Domain;
using Haworks.Contracts.Payments;
using Xunit;

namespace Haworks.Payments.Unit.Domain;

public class PaymentTests
{
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly string _userId = "user-123";
    private readonly decimal _amount = 100.00m;
    private readonly decimal _tax = 8.00m;
    private readonly string _currency = "USD";
    private readonly PaymentProvider _provider = PaymentProvider.Stripe;
    private readonly Guid _sagaId = Guid.NewGuid();

    private Payment CreateDefault() =>
        Payment.Create(_orderId, _userId, _amount, _tax, _currency, _provider, _sagaId);

    private Payment CreateCompleted()
    {
        var p = CreateDefault();
        p.AttachProviderSession("sess_1", "url");
        p.MarkCompleted("tx_1", "card");
        return p;
    }

    #region Factory Method Tests

    [Fact]
    public void Create_WithValidParameters_CreatesPaymentWithCorrectValues()
    {
        var payment = CreateDefault();
        payment.Id.Should().NotBeEmpty();
        payment.OrderId.Should().Be(_orderId);
        payment.UserId.Should().Be(_userId);
        payment.Amount.Should().Be(_amount);
        payment.Tax.Should().Be(_tax);
        payment.Currency.Should().Be(_currency);
        payment.Provider.Should().Be(_provider);
        payment.SagaId.Should().Be(_sagaId);
        payment.Status.Should().Be(PaymentStatus.Pending);
    }

    [Fact]
    public void Create_WithNegativeAmount_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(_orderId, _userId, -1m, _tax, _currency, _provider, _sagaId));
    }

    [Fact]
    public void Create_with_negative_tax_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(_orderId, _userId, _amount, -1m, _currency, _provider, _sagaId));
    }

    [Fact]
    public void Create_with_empty_userId_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(_orderId, "", _amount, _tax, _currency, _provider, _sagaId));
    }

    [Fact]
    public void Create_with_empty_orderId_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(Guid.Empty, _userId, _amount, _tax, _currency, _provider, _sagaId));
    }

    [Fact]
    public void Create_with_None_provider_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Payment.Create(_orderId, _userId, _amount, _tax, _currency, PaymentProvider.None, _sagaId));
    }

    #endregion

    #region Status Behavior Tests

    [Fact]
    public void AttachProviderSession_SetsStatusToProcessing()
    {
        var payment = CreateDefault();
        payment.AttachProviderSession("sess_123", "url");
        payment.Status.Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public void MarkCompleted_SetsIsCompleteAndStatus()
    {
        var payment = CreateDefault();
        payment.MarkCompleted("tx_456", "card");
        payment.IsComplete.Should().BeTrue();
        payment.Status.Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public void MarkCompleted_twice_throws()
    {
        var payment = CreateCompleted();
        var act = () => payment.MarkCompleted("tx_2", "card");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Payment is already completed");
    }

    [Fact]
    public void MarkCompleted_on_Failed_throws()
    {
        var payment = CreateDefault();
        payment.MarkFailed();
        var act = () => payment.MarkCompleted("tx_1", "card");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed*");
    }

    [Fact]
    public void MarkCompleted_on_Cancelled_throws()
    {
        var payment = CreateDefault();
        payment.MarkCancelled();
        var act = () => payment.MarkCompleted("tx_1", "card");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cancelled*");
    }

    [Fact]
    public void MarkRefunded_SetsStatusToRefunded()
    {
        var payment = CreateCompleted();
        payment.MarkRefunded();
        payment.Status.Should().Be(PaymentStatus.Refunded);
    }

    [Fact]
    public void MarkRefunded_on_Pending_throws()
    {
        var payment = CreateDefault();
        var act = () => payment.MarkRefunded();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Pending*");
    }

    [Fact]
    public void MarkRefunded_on_Failed_throws()
    {
        var payment = CreateDefault();
        payment.MarkFailed();
        var act = () => payment.MarkRefunded();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed*");
    }

    [Fact]
    public void MarkRefunded_on_Cancelled_throws()
    {
        var payment = CreateDefault();
        payment.MarkCancelled();
        var act = () => payment.MarkRefunded();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cancelled*");
    }

    [Fact]
    public void MarkRefunded_on_already_Refunded_throws()
    {
        var payment = CreateCompleted();
        payment.MarkRefunded();
        var act = () => payment.MarkRefunded();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Refunded*");
    }

    #endregion
}
