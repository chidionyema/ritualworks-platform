using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure.Options;
using Haworks.Payments.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Haworks.Payments.Unit;

public class PaymentGatewayTests
{
    private readonly Mock<ICheckoutSessionService> _checkoutMock;
    private readonly Mock<IWebhookProcessor> _webhooksMock;
    private readonly Mock<ILogger<PaymentGateway>> _loggerMock;

    public PaymentGatewayTests()
    {
        _checkoutMock = new Mock<ICheckoutSessionService>();
        _webhooksMock = new Mock<IWebhookProcessor>();
        _loggerMock = new Mock<ILogger<PaymentGateway>>();
    }

    private PaymentGateway CreateGateway(PaymentProvider provider)
    {
        var options = Options.Create(new PaymentProviderOptions
        {
            Active = provider
        });

        return new PaymentGateway(
            options,
            _checkoutMock.Object,
            _webhooksMock.Object);
    }

    [Fact]
    public void Constructor_SetsActiveProvider()
    {
        var gateway = CreateGateway(PaymentProvider.Stripe);
        Assert.Equal(PaymentProvider.Stripe, gateway.ActiveProvider);
    }

    [Fact]
    public void Constructor_ExposesServices()
    {
        var gateway = CreateGateway(PaymentProvider.Stripe);
        Assert.Same(_checkoutMock.Object, gateway.Checkout);
        Assert.Same(_webhooksMock.Object, gateway.Webhooks);
    }
}
