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

    #region Factory Method Tests

    [Fact]
    public void Create_WithValidParameters_CreatesPaymentWithCorrectValues()
    {
        var payment = Payment.Create(_orderId, _userId, _amount, _tax, _currency, _provider, _sagaId);
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

    #endregion

    #region Status Behavior Tests

    [Fact]
    public void AttachProviderSession_SetsStatusToProcessing()
    {
        var payment = Payment.Create(_orderId, _userId, _amount, _tax, _currency, _provider, _sagaId);
        payment.AttachProviderSession("sess_123", "url");
        payment.Status.Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public void MarkCompleted_SetsIsCompleteAndStatus()
    {
        var payment = Payment.Create(_orderId, _userId, _amount, _tax, _currency, _provider, _sagaId);
        payment.MarkCompleted("tx_456", "card");
        payment.IsComplete.Should().BeTrue();
        payment.Status.Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public void MarkRefunded_SetsStatusToRefunded()
    {
        var payment = Payment.Create(_orderId, _userId, _amount, _tax, _currency, _provider, _sagaId);
        payment.MarkRefunded();
        payment.Status.Should().Be(PaymentStatus.Refunded);
    }

    #endregion
}
