using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Contracts.Payments;
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
public class SubscriptionSagaTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public SubscriptionSagaTests(PaymentsWebAppFactory factory)
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
    public async Task SubscriptionStarted_Should_StartSaga_And_Active()
    {
        // Arrange
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var providerSubId = $"sub_{Guid.NewGuid():N}";
        var userId = "user_123";
        var planId = "plan_premium";
        var periodEnd = DateTime.UtcNow.AddDays(30);

        // Act
        await harness.Bus.Publish(new SubscriptionStartedEvent
        {
            SubscriptionId = providerSubId,
            UserId = userId,
            PlanId = planId,
            Provider = PaymentProvider.Stripe,
            CurrentPeriodEnd = periodEnd
        });

        // Assert
        await Task.Delay(2000);

        var sagaHarness = harness.GetSagaStateMachineHarness<SubscriptionSaga, SubscriptionSagaState>();
        
        (await sagaHarness.Consumed.Any<SubscriptionStartedEvent>())
            .Should().BeTrue("Saga should have consumed SubscriptionStartedEvent");

        var instance = sagaHarness.Sagas.Select(x => x.ProviderSubscriptionId == providerSubId).FirstOrDefault();
        instance.Should().NotBeNull("Saga instance should have been created");
        instance!.Saga.CurrentState.Should().Be("Active");
    }

    [Fact]
    public async Task RenewalFailed_Should_Transition_To_GracePeriod_And_Dunning()
    {
        // Arrange
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var sagaHarness = harness.GetSagaStateMachineHarness<SubscriptionSaga, SubscriptionSagaState>();
        var providerSubId = $"sub_fail_{Guid.NewGuid():N}";
        
        await harness.Bus.Publish(new SubscriptionStartedEvent
        {
            SubscriptionId = providerSubId,
            UserId = "user_fail",
            PlanId = "plan_fail",
            Provider = PaymentProvider.Stripe,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30)
        });

        await Task.Delay(2000);
        var instance = sagaHarness.Sagas.Select(x => x.ProviderSubscriptionId == providerSubId).FirstOrDefault();
        var sagaId = instance!.Saga.CorrelationId;

        // Act: Emit failure (In 'Active' state, it should transition to 'GracePeriod')
        await harness.Bus.Publish(new SubscriptionRenewalFailedEvent
        {
            SubscriptionId = sagaId,
            ErrorCode = "CardDeclined",
            ErrorMessage = "Your card has expired"
        });

        // Assert
        await Task.Delay(2000);
        
        var updatedInstance = sagaHarness.Sagas.Select(x => x.CorrelationId == sagaId).FirstOrDefault();
        updatedInstance.Should().NotBeNull();
        updatedInstance!.Saga.CurrentState.Should().Be("GracePeriod");
        updatedInstance.Saga.RetryCount.Should().Be(1);
            
        // Verify grace period event published
        (await harness.Published.Any<SubscriptionGracePeriodStartedEvent>(x => x.Context.Message.SubscriptionId == sagaId))
            .Should().BeTrue("Grace period event should have been published");
    }
}
