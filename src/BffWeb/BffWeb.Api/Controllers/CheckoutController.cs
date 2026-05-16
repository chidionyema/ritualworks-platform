using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Haworks.BffWeb.Application.Telemetry;
using Haworks.BuildingBlocks.Idempotency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// The public checkout flow:
///   1. Browser POSTs /api/checkout with cart items.
///   2. bff-web allocates a sagaId + orderId, forwards to checkout-svc'
///      <c>POST /api/checkouts</c>.
///   3. Returns {sagaId, orderId} immediately.
///   4. Browser opens a SignalR connection to /hubs/checkout and
///      subscribes to its sagaId.
///   5. The CheckoutSaga publishes StockReservationRequested →
///      catalog reserves → publishes StockReserved → CheckoutSaga
///      publishes PaymentSessionRequested → payments creates session
///      → publishes PaymentSessionCreated → bff-web's
///      PaymentSessionCreatedConsumer pushes the URL via SignalR.
///   6. Browser navigates to the checkout URL.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CheckoutController(
    IHttpClientFactory httpClientFactory,
    ILogger<CheckoutController> logger) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting("expensive")]
    public async Task<IActionResult> Start([FromBody] CheckoutRequest body, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        using var activity = BffWebActivities.Source.StartActivity("bff.checkout.start");
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("customer.id", userId);
        activity?.SetTag("checkout.total_amount_cents", (long)Math.Round(body.TotalAmount * 100m, 0, MidpointRounding.AwayFromZero));
        activity?.SetTag("checkout.item_count", body.Items.Count);

        // User-scoped idempotency key. Every retry of the *same* checkout from the
        // *same* user collapses to the same key; a different user's replay with
        // the same client-supplied nonce can't collide because UserId is in the
        // hash. Per .claude/rules/dotnet-clean-arch.md "MANDATORY: SHA-256
        // Idempotency".
        var cartShape = string.Join(
            ",",
            body.Items
                .OrderBy(i => i.ProductId)
                .Select(i => $"{i.ProductId}:{i.Quantity}:{i.UnitPrice.ToString("F2", CultureInfo.InvariantCulture)}"));
        var idempotencyKey = IdempotencyKey.Derive(
            userId: userId,
            operation: "checkout.start",
            body.TotalAmount.ToString("F2", CultureInfo.InvariantCulture),
            cartShape,
            body.IdempotencyKey ?? string.Empty);

        var client = httpClientFactory.CreateClient(BackendClients.Checkout);
        var resp = await client.PostAsJsonAsync("/api/checkouts", new
        {
            sagaId,
            orderId,
            userId = userId,
            customerEmail = body.CustomerEmail,
            totalAmount = body.TotalAmount,
            idempotencyKey,
            items = body.Items,
        }, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var payload = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "checkout-svc rejected saga start: {StatusCode} {Body}", resp.StatusCode, payload);
            return StatusCode((int)resp.StatusCode, new { error = "checkout-svc rejected the request", detail = payload });
        }

        return Accepted(new { sagaId, orderId, message = "subscribe to /hubs/checkout to receive the payment URL" });
    }
}

public sealed record CheckoutRequest(
    string CustomerEmail,
    decimal TotalAmount,
    string? IdempotencyKey,
    IReadOnlyList<CheckoutLineItem> Items);

public sealed record CheckoutLineItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);
