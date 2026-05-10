using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Haworks.Orders.Integration;

/// <summary>
/// HTTP-layer idempotency middleware end-to-end. Exercises the actual
/// Orders.Api pipeline so we verify auth + middleware + Postgres
/// idempotency_claims store interact correctly. The middleware sits
/// after UseAuthentication so it can scope keys by UserId; the
/// TestAuthenticationHandler injects a fixed test-user identity.
/// </summary>
[Collection("Orders Integration")]
public sealed class IdempotencyMiddlewareTests(OrdersWebAppFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.EnsureSchemaAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static HttpContent MinimalOrderBody() =>
        // Body content is intentionally minimal — the test cares about the
        // middleware's short-circuit behavior, not the handler outcome.
        // Whether the validator rejects or the handler accepts, the second
        // POST with the same key must NOT reach the handler.
        JsonContent.Create(new
        {
            userId = "test-user",
            customerEmail = "buyer@example.com",
            totalAmount = 9.99m,
            currency = "USD",
            sagaId = Guid.NewGuid(),
            idempotencyKey = "client-side-nonce",
            items = Array.Empty<object>(),
        });

    private static string FreshKey() =>
        // Fresh per-test so concurrent runs don't see each other's claims.
        $"test-{Guid.NewGuid():N}";

    [Fact]
    public async Task Post_without_idempotency_key_passes_through_to_handler()
    {
        // No header → middleware no-ops. The handler runs and returns
        // whatever it would have (400/201/etc). Crucially: NOT 409.
        var resp = await _client.PostAsync("/api/orders", MinimalOrderBody());

        resp.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Post_with_first_idempotency_key_passes_through_to_handler()
    {
        var key = FreshKey();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = MinimalOrderBody() };
        req.Headers.Add("X-Idempotency-Key", key);

        var resp = await _client.SendAsync(req);

        // First call wins — middleware lets it through. Whatever the handler
        // returns is fine; we only assert it's not the middleware's 409.
        resp.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Second_post_with_same_idempotency_key_returns_409()
    {
        var key = FreshKey();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = MinimalOrderBody() };
        first.Headers.Add("X-Idempotency-Key", key);
        await _client.SendAsync(first);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = MinimalOrderBody() };
        second.Headers.Add("X-Idempotency-Key", key);
        var resp = await _client.SendAsync(second);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        resp.Headers.GetValues("Idempotency-Status").Should().ContainSingle(v => v == "duplicate");
    }

    [Fact]
    public async Task Different_idempotency_keys_both_pass_through()
    {
        // Different keys for the same user are independent claims — the
        // store keys off SHA256(userId, path, clientKey) so each is unique.
        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = MinimalOrderBody() };
        first.Headers.Add("X-Idempotency-Key", FreshKey());
        var resp1 = await _client.SendAsync(first);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = MinimalOrderBody() };
        second.Headers.Add("X-Idempotency-Key", FreshKey());
        var resp2 = await _client.SendAsync(second);

        resp1.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        resp2.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_request_with_header_is_ignored_by_middleware()
    {
        // Middleware only acts on mutating methods (POST/PUT/PATCH/DELETE).
        // GET requests pass through even when the header is present —
        // GETs should be naturally idempotent and don't need a claim.
        var key = FreshKey();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{Guid.NewGuid()}");
        req.Headers.Add("X-Idempotency-Key", key);

        var resp1 = await _client.SendAsync(req);

        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/{Guid.NewGuid()}");
        req2.Headers.Add("X-Idempotency-Key", key);
        var resp2 = await _client.SendAsync(req2);

        // Both should hit the handler (likely 404 since the order doesn't
        // exist). Critically: neither is the middleware's 409.
        resp1.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        resp2.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
    }
}
