using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Payments;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;

namespace Haworks.Payments.Integration;

/// <summary>
/// Integration coverage for /webhooks/stripe end-to-end:
///   • Signature validation
///   • Outbox-published PaymentWebhookValidatedEvent
///   • In-process consumer transitions Payment + publishes downstream event
///   • Idempotency: replaying the same Stripe EventId 3× yields exactly one
///     state transition (one PaymentCompletedEvent published).
/// </summary>
public sealed class WebhookFlowsTests : IClassFixture<PaymentsWebAppFactory>, IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public WebhookFlowsTests(PaymentsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        // EF retry-on-failure (5 × 500ms) plus actual processing can push
        // a consume past the 5s default TestTimeout. Bump per-test timeout
        // generously — the tests are still cheap because in-memory transport.
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_returns_200()
    {
        var resp = await _client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stripe_webhook_with_invalid_signature_returns_400_and_does_not_publish()
    {
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var publishedBefore = harness.Published.Select<PaymentWebhookValidatedEvent>().Count();

        var payload = "{\"id\":\"evt_invalid\",\"type\":\"checkout.session.completed\",\"data\":{\"object\":{\"id\":\"sess_1\"}}}";
        var resp = await PostStripeAsync(payload, signature: "t=1,v1=deadbeef");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        harness.Published.Select<PaymentWebhookValidatedEvent>().Count().Should().Be(publishedBefore);
    }

    [Fact]
    public async Task Stripe_webhook_missing_signature_returns_400()
    {
        var resp = await _client.PostAsync("/webhooks/stripe",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stripe_webhook_valid_signature_publishes_PaymentWebhookValidatedEvent()
    {
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var eventId = "evt_valid_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", "sess_x", paidMinor: 0);

        var resp = await PostStripeAsync(payload, PaymentsWebAppFactory.SignStripe(payload));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await harness.Published.Any<PaymentWebhookValidatedEvent>()).Should().BeTrue();

        var ctx = harness.Published.Select<PaymentWebhookValidatedEvent>()
            .FirstOrDefault(p => p.Context.Message.ProviderEventId == eventId);
        ctx.Should().NotBeNull();
        ctx!.Context.Message.Provider.Should().Be("Stripe");
        ctx.Context.Message.EventType.Should().Be("checkout.session.completed");
    }

    [Fact]
    public async Task Stripe_webhook_for_known_payment_session_publishes_PaymentCompletedEvent()
    {
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        var sessionId = "sess_known_" + Guid.NewGuid().ToString("N");
        var payment = await SeedPendingPaymentAsync(sessionId, amount: 50m);

        var eventId = "evt_complete_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", sessionId, paidMinor: 5000);

        var resp = await PostStripeAsync(payload, PaymentsWebAppFactory.SignStripe(payload));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Wait for the consumer to process the validated event.
        (await harness.Consumed.Any<PaymentWebhookValidatedEvent>()).Should().BeTrue();

        // Surface any consumer fault before chasing publication assertions —
        // a thrown exception inside Consume() shows up here, not as a missing
        // published event, and the bare "Expected … not to be null" message
        // hides the underlying root cause.
        var consumerHarness = harness.GetConsumerHarness<Haworks.Payments.Application.Consumers.PaymentWebhookValidatedConsumer>();
        var faults = consumerHarness.Consumed.Select<PaymentWebhookValidatedEvent>()
            .Where(c => c.Exception is not null).ToList();
        faults.Should().BeEmpty(string.Join(" | ",
            faults.Select(f => $"{f.Exception?.GetType().Name}: {f.Exception?.Message}")));

        // Downstream PaymentCompletedEvent should land for our payment.
        var completed = harness.Published.Select<PaymentCompletedEvent>()
            .FirstOrDefault(p => p.Context.Message.PaymentId == payment.Id);
        completed.Should().NotBeNull("the consumer must publish PaymentCompletedEvent");
        completed!.Context.Message.OrderId.Should().Be(payment.OrderId);
        completed.Context.Message.SagaId.Should().Be(payment.SagaId);
        completed.Context.Message.Amount.Should().Be(50m);
        completed.Context.Message.Provider.Should().Be("Stripe");

        // Payment row in DB transitioned to Completed.
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var stored = await db.Payments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        stored.IsComplete.Should().BeTrue();
        stored.Status.Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public async Task Stripe_webhook_amount_mismatch_flags_payment_and_publishes_mismatch_event()
    {
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        var sessionId = "sess_mismatch_" + Guid.NewGuid().ToString("N");
        var payment = await SeedPendingPaymentAsync(sessionId, amount: 50m);

        // Stripe captures 75 instead of 50 -> amount mismatch.
        var eventId = "evt_mismatch_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", sessionId, paidMinor: 7500);

        var resp = await PostStripeAsync(payload, PaymentsWebAppFactory.SignStripe(payload));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await harness.Consumed.Any<PaymentWebhookValidatedEvent>()).Should().BeTrue();

        var mismatch = harness.Published.Select<PaymentAmountMismatchEvent>()
            .FirstOrDefault(p => p.Context.Message.PaymentId == payment.Id);
        mismatch.Should().NotBeNull();
        mismatch!.Context.Message.ActualPaid.Should().Be(75m);
        mismatch.Context.Message.ExpectedTotal.Should().Be(50m);
        mismatch.Context.Message.Difference.Should().Be(25m);

        // Payment must be Flagged, not Completed.
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var stored = await db.Payments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        stored.Status.Should().Be(PaymentStatus.Flagged);
        stored.IsComplete.Should().BeFalse();
        // No PaymentCompletedEvent for this payment.
        harness.Published.Select<PaymentCompletedEvent>()
            .Any(p => p.Context.Message.PaymentId == payment.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Webhook_idempotency_replaying_same_eventId_3x_yields_one_PaymentCompleted()
    {
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        var sessionId = "sess_idem_" + Guid.NewGuid().ToString("N");
        var payment = await SeedPendingPaymentAsync(sessionId, amount: 25m);

        var eventId = "evt_idempotent_" + Guid.NewGuid().ToString("N");
        var payload = StripePayload(eventId, "checkout.session.completed", sessionId, paidMinor: 2500);
        var signature = PaymentsWebAppFactory.SignStripe(payload);

        // Replay 3x.
        for (var i = 0; i < 3; i++)
        {
            var resp = await PostStripeAsync(payload, signature);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "Stripe expects 200 for idempotent redeliveries");
        }

        // Wait for consumption to complete; expect exactly one published
        // PaymentCompletedEvent for our payment regardless of replays.
        (await harness.Consumed.Any<PaymentWebhookValidatedEvent>()).Should().BeTrue();

        var completedCount = harness.Published.Select<PaymentCompletedEvent>()
            .Count(p => p.Context.Message.PaymentId == payment.Id);
        completedCount.Should().Be(1,
            "MT inbox dedupes by MessageId (sha256(provider:eventId)); replays must not produce duplicate downstream events");

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var stored = await db.Payments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        stored.IsComplete.Should().BeTrue();
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

    /// <summary>
    /// Builds a synthetic Stripe webhook payload sufficient for the
    /// consumer's parser. paidMinor=0 means "amount field absent".
    /// </summary>
    private static string StripePayload(string eventId, string eventType, string sessionId, long paidMinor)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["type"] = eventType,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = BuildObject(sessionId, paidMinor),
            }
        };
        return JsonSerializer.Serialize(dict);

        static Dictionary<string, object?> BuildObject(string sid, long minor)
        {
            var inner = new Dictionary<string, object?>
            {
                ["id"] = sid,
                ["payment_intent"] = "pi_" + sid,
                ["payment_method_types"] = new[] { "card" },
            };
            if (minor > 0) inner["amount_total"] = minor;
            return inner;
        }
    }
}
