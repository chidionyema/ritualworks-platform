using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.Payments.Infrastructure.Options;
using Haworks.Payments.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.Payments.Unit;

public class PaymentGatewayTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<ICheckoutSessionService> _checkoutMock = new();
    private readonly Mock<ISubscriptionManager> _subscriptionsMock = new();
    private readonly Mock<IRefundService> _refundsMock = new();

    public PaymentGatewayTests()
    {
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(Haworks.Payments.Infrastructure.Stripe.StripeCheckoutSessionService))).Returns(_checkoutMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(Haworks.Payments.Infrastructure.Stripe.StripeSubscriptionManager))).Returns(_subscriptionsMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(Haworks.Payments.Infrastructure.Stripe.StripeRefundService))).Returns(_refundsMock.Object);
    }

    private PaymentGateway CreateGateway(PaymentProvider provider)
    {
        var options = Options.Create(new PaymentProviderOptions
        {
            Active = provider
        });

        return new PaymentGateway(_serviceProviderMock.Object, options, new Mock<ILogger<PaymentGateway>>().Object);
    }

    [Fact]
    public void Constructor_SetsActiveProvider()
    {
        var gateway = CreateGateway(PaymentProvider.Stripe);
        Assert.Equal(PaymentProvider.Stripe, gateway.ActiveProvider);
    }
}
