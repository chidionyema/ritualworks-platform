using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Contracts.Payments;
using Haworks.Payments.Api.Controllers;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Payments.Integration;

[Collection("Payments Integration")]
public class RefundEdgeCaseTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public RefundEdgeCaseTests(PaymentsWebAppFactory factory)
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

    private async Task<Payment> SeedPayment(decimal amount, bool complete)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var payment = Payment.Create(
            Guid.NewGuid(), "user_edge", amount, 0, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());

        if (complete)
        {
            payment.AttachProviderSession("sess_edge", "url");
            payment.MarkCompleted("pi_edge_" + Guid.NewGuid().ToString("N")[..8], "card");
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private async Task<Payment> SeedFailedPayment()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var payment = Payment.Create(
            Guid.NewGuid(), "user_edge", 100m, 0, "USD",
            PaymentProvider.Stripe, Guid.NewGuid());
        payment.MarkFailed();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    [Fact]
    public async Task Refund_exceeding_payment_amount_is_rejected()
    {
        var payment = await SeedPayment(50m, complete: true);

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, 75m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Partial_refund_within_amount_accepted()
    {
        var payment = await SeedPayment(100m, complete: true);

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, 30m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refund_of_pending_payment_rejected()
    {
        var payment = await SeedPayment(100m, complete: false);

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, 50m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refund_of_failed_payment_rejected()
    {
        var payment = await SeedFailedPayment();

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, 50m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refund_of_nonexistent_payment_returns_not_found()
    {
        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(Guid.NewGuid(), 10m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refund_with_zero_amount_rejected()
    {
        var payment = await SeedPayment(100m, complete: true);

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, 0m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refund_with_negative_amount_rejected()
    {
        var payment = await SeedPayment(100m, complete: true);

        var response = await _client.PostAsJsonAsync("/api/refunds",
            new CreateRefundRequest(payment.Id, -5m, "USD"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
