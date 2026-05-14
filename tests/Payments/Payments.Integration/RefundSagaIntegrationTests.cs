using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Contracts.Payments;
using Haworks.Payments.Api.Controllers;
using Haworks.Payments.Application.Queries.Refunds;
using Haworks.Payments.Application.Sagas;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MassTransit.Testing;
using Microsoft.Extensions.Logging;
using Moq;

namespace Haworks.Payments.Integration;

[Collection("Payments Integration")]
public class RefundSagaIntegrationTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public RefundSagaIntegrationTests(PaymentsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateRefund_Should_StartSaga_And_ReachAwaitingProvider()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        
        var payment = Payment.Create(
            Guid.NewGuid(), 
            "user_123", 
            100.00m, 
            0, 
            "USD", 
            PaymentProvider.Stripe,
            Guid.NewGuid());
        
        payment.MarkCompleted("pi_test_123", "card");
        
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var request = new CreateRefundRequest(
            PaymentId: payment.Id,
            Amount: 50.00m,
            Currency: "USD",
            Reason: "Test refund",
            RequestedBy: "TestRunner"
        );

        // Act: Call API
        var response = await _client.PostAsJsonAsync("/api/refunds", request);
        response.EnsureSuccessStatusCode();
        var refundId = await response.Content.ReadFromJsonAsync<Guid>();

        // Assert
        // Give MassTransit time to process
        await Task.Delay(2000);

        (await harness.Published.Any<RefundRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("RefundRequestedEvent should have been published by the API handler");

        var sagaHarness = harness.GetSagaStateMachineHarness<RefundSaga, RefundSagaState>();
        
        (await sagaHarness.Consumed.Any<RefundRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("Saga should have consumed RefundRequestedEvent");

        (await sagaHarness.Created.Any(x => x.CorrelationId == refundId))
            .Should().BeTrue("Saga instance should have been created");

        (await harness.Published.Any<ProviderRefundInitiationRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("Saga should have published ProviderRefundInitiationRequestedEvent");
    }
}
