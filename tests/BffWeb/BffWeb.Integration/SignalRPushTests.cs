using System.Net;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BffWeb.Api.SignalR;
using Haworks.Contracts.Payments;

namespace Haworks.BffWeb.Integration;

/// <summary>
/// Phase 7's centerpiece: a SignalR client subscribes to a saga group,
/// the test publishes a PaymentSessionCreatedEvent through MT's in-memory
/// transport, and the consumer pushes the checkout URL into the group —
/// the SignalR client receives it via the "CheckoutReady" handler.
///
/// Proves the bff-web bridge: RabbitMQ event -> MT consumer -> SignalR
/// hub group push -> connected browser -> ready to redirect to Stripe.
/// </summary>
public sealed class SignalRPushTests : IClassFixture<BffWebFactory>, IAsyncLifetime
{
    private readonly BffWebFactory _factory;

    public SignalRPushTests(BffWebFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        // Spin up the harness so the consumer endpoint is wired before
        // tests start publishing events.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        return harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_returns_200()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SignalR_client_subscribed_to_sagaId_receives_CheckoutReady_when_PaymentSessionCreated_publishes()
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        // Connect a SignalR client through the WAF's TestServer.
        var hubConnection = await ConnectHubAsync();

        // Latch — set when the hub pushes "CheckoutReady".
        var receivedUrl = new TaskCompletionSource<string>();
        hubConnection.On<CheckoutReadyMessage>("CheckoutReady", msg =>
        {
            if (msg.SagaId == sagaId)
            {
                receivedUrl.TrySetResult(msg.CheckoutUrl);
            }
        });

        // Subscribe to our saga's group.
        await hubConnection.InvokeAsync("SubscribeToSaga", sagaId);

        // Publish the event the saga would have published. The
        // PaymentSessionCreatedConsumer reads it from the MT in-memory
        // transport and pushes into the SignalR group.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await publisher.Publish(new PaymentSessionCreatedEvent
            {
                OrderId = orderId,
                SagaId = sagaId,
                PaymentId = paymentId,
                UserId = "test-user-id",
                SessionId = "sess_test",
                CheckoutUrl = "https://stripe.test/sess_test",
                Provider = "Stripe",
                Amount = 25.50m,
                Currency = "USD",
            });
        }

        // The push should land within a few seconds — generous timeout to
        // absorb harness scheduling on a busy CI box.
        var url = await WaitForAsync(receivedUrl.Task, TimeSpan.FromSeconds(15));
        url.Should().Be("https://stripe.test/sess_test");

        await hubConnection.StopAsync();
    }

    [Fact]
    public async Task PaymentSessionCreatedEvent_for_a_different_sagaId_does_NOT_land_in_a_clients_subscription()
    {
        var subscribedSagaId = Guid.NewGuid();
        var otherSagaId = Guid.NewGuid();

        var hubConnection = await ConnectHubAsync();

        var unwantedReceived = false;
        hubConnection.On<CheckoutReadyMessage>("CheckoutReady", msg =>
        {
            if (msg.SagaId == otherSagaId) unwantedReceived = true;
        });

        await hubConnection.InvokeAsync("SubscribeToSaga", subscribedSagaId);

        // Publish an event for a DIFFERENT saga — the SignalR group filter
        // should mean our subscriber doesn't see it.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await publisher.Publish(new PaymentSessionCreatedEvent
            {
                OrderId = Guid.NewGuid(),
                SagaId = otherSagaId,
                PaymentId = Guid.NewGuid(),
                UserId = "other-user-id",
                SessionId = "sess_other",
                CheckoutUrl = "https://stripe.test/sess_other",
                Provider = "Stripe",
                Amount = 10m,
                Currency = "USD",
            });
        }

        // Wait for the consumer to process the message. Use Published (which
        // completes synchronously on in-memory transport) + a settling delay
        // instead of Consumed.Any which can miss the window when this test
        // runs before the harness consumer endpoint is fully warmed.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await harness.Published.Any<PaymentSessionCreatedEvent>(cts.Token)).Should().BeTrue();
        // Allow the consumer to finish processing and any SignalR push to settle.
        await Task.Delay(2000);

        unwantedReceived.Should().BeFalse(
            "SignalR group push for a different sagaId must not reach this client");

        await hubConnection.StopAsync();
    }

    private async Task<HubConnection> ConnectHubAsync()
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/checkout"), options =>
            {
                // Route the SignalR negotiation through the WAF's in-memory
                // server rather than a real network socket.
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task<T> WaitForAsync<T>(Task<T> task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var completed = await Task.WhenAny(task, Task.Delay(timeout, cts.Token));
        if (completed != task)
        {
            throw new TimeoutException($"Did not complete within {timeout.TotalSeconds}s");
        }
        await cts.CancelAsync();
        return await task;
    }

    private sealed record CheckoutReadyMessage(
        Guid SagaId, Guid OrderId, Guid PaymentId,
        string CheckoutUrl, string Provider, decimal Amount, string Currency);
}
