using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;

namespace Haworks.Payments.Integration;

/// <summary>
/// Dedicated integration coverage for webhook idempotency scenarios,
/// porting behavior from the monolith's WebhookIdempotencyGuardTests.
/// 
/// The platform uses a combination of:
/// 1. MassTransit Inbox (transport-level deduplication)
/// 2. WebhookEvent unique index (database-level atomic deduplication)
/// 3. Application-level check in PaymentWebhookValidatedConsumer
/// </summary>
public sealed class WebhookIdempotencyTests : IClassFixture<PaymentsWebAppFactory>, IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public WebhookIdempotencyTests(PaymentsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Stripe_DuplicateWebhooks_AreProcessedExactlyOnce()
    {
        // Arrange
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var sessionId = "sess_idem_" + Guid.NewGuid().ToString("N");
        var payment = await SeedPendingPaymentAsync(sessionId, amount: 100m);

        var eventId = "evt_idem_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", sessionId, paidMinor: 10000);
        var signature = PaymentsWebAppFactory.SignStripe(payload);

        // Act - Send the same webhook 3 times
        for (var i = 0; i < 3; i++)
        {
            var resp = await PostStripeAsync(payload, signature);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Assert - Wait for completion
        await PollUntilAsync(
            async () =>
            {
                await using var s = _factory.Services.CreateAsyncScope();
                var db = s.ServiceProvider.GetRequiredService<PaymentDbContext>();
                var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payment.Id);
                return p?.IsComplete == true;
            },
            TimeSpan.FromSeconds(30));

        // Verify database state
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var stored = await db.Payments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        stored.IsComplete.Should().BeTrue();
        stored.Status.Should().Be(PaymentStatus.Completed);

        var webhookRows = await db.WebhookEvents.AsNoTracking()
            .CountAsync(w => w.Provider == PaymentProvider.Stripe && w.ProviderEventId == eventId);
        
        webhookRows.Should().Be(1, 
            "The unique index on (Provider, ProviderEventId) must prevent duplicate processing rows.");
    }

    [Fact]
    public async Task Stripe_ConcurrentWebhooks_RaceIsResolvedByDatabaseIndex()
    {
        // Arrange
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var sessionId = "sess_race_" + Guid.NewGuid().ToString("N");
        var payment = await SeedPendingPaymentAsync(sessionId, amount: 50m);

        var eventId = "evt_race_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", sessionId, paidMinor: 5000);
        var signature = PaymentsWebAppFactory.SignStripe(payload);

        // Act - Fire multiple requests concurrently
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => PostStripeAsync(payload, signature))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        // Assert
        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.OK));

        await PollUntilAsync(
            async () =>
            {
                await using var s = _factory.Services.CreateAsyncScope();
                var db = s.ServiceProvider.GetRequiredService<PaymentDbContext>();
                var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == payment.Id);
                return p?.IsComplete == true;
            },
            TimeSpan.FromSeconds(30));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var webhookRows = await db.WebhookEvents.AsNoTracking()
            .CountAsync(w => w.Provider == PaymentProvider.Stripe && w.ProviderEventId == eventId);
        
        webhookRows.Should().Be(1, "Only one winner should exist in the WebhookEvents table after a race.");
    }

    private async Task<HttpResponseMessage> PostStripeAsync(string payload, string signature)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = content };
        req.Headers.Add("Stripe-Signature", signature);
        return await _client.SendAsync(req);
    }

    private async Task<Payment> SeedPendingPaymentAsync(string sessionId, decimal amount)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = Payment.Create(
            orderId: Guid.NewGuid(),
            userId: "user-test",
            amount: amount,
            tax: 0m,
            currency: "USD",
            provider: PaymentProvider.Stripe,
            sagaId: Guid.NewGuid());
        payment.AttachProviderSession(sessionId, $"https://stripe.test/{sessionId}");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private static string StripePayload(string eventId, string eventType, string sessionId, long paidMinor)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["type"] = eventType,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = sessionId,
                    ["payment_intent"] = "pi_" + sessionId,
                    ["payment_method_types"] = new[] { "card" },
                    ["amount_total"] = paidMinor
                },
            }
        };
        return JsonSerializer.Serialize(dict);
    }

    private static async Task PollUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(250);
        }
    }
}
