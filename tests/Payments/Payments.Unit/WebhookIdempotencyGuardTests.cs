using Haworks.Payments.Application.Interfaces;
using Haworks.Contracts.Payments;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Haworks.Payments.Infrastructure.Webhooks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Haworks.Payments.Unit;

public class WebhookIdempotencyGuardTests
{
    private readonly Mock<IPaymentRepository> _paymentRepositoryMock;
    private readonly Mock<IDistributedCache> _cacheMock;
    private readonly Mock<ILogger<WebhookIdempotencyGuard>> _loggerMock;
    private readonly WebhookIdempotencyGuard _guard;

    public WebhookIdempotencyGuardTests()
    {
        _paymentRepositoryMock = new Mock<IPaymentRepository>();
        _cacheMock = new Mock<IDistributedCache>();
        _loggerMock = new Mock<ILogger<WebhookIdempotencyGuard>>();
        _guard = new WebhookIdempotencyGuard(_paymentRepositoryMock.Object, _cacheMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task IsAlreadyProcessedAsync_WithEmptyEventId_ReturnsFalse()
    {
        var result = await _guard.IsAlreadyProcessedAsync(PaymentProvider.Stripe, "", CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task IsAlreadyProcessedAsync_CacheHit_ReturnsTrue()
    {
        _cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Encoding.UTF8.GetBytes("1"));

        var result = await _guard.IsAlreadyProcessedAsync(PaymentProvider.Stripe, "evt_123", CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task IsAlreadyProcessedAsync_DbHit_ReturnsTrueAndBackfillsCache()
    {
        _cacheMock.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _paymentRepositoryMock.Setup(r => r.WebhookEventExistsAsync(PaymentProvider.Stripe, "evt_123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _guard.IsAlreadyProcessedAsync(PaymentProvider.Stripe, "evt_123", CancellationToken.None);

        Assert.True(result);
        _cacheMock.Verify(c => c.SetAsync(
            It.Is<string>(s => s.Contains("evt_123")),
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
