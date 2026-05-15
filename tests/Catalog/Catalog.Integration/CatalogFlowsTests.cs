using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Integration;

/// <summary>
/// End-to-end catalog-svc tests against a real Postgres (Testcontainers) +
/// in-memory MassTransit. Asserts both HTTP behavior and event publication.
/// </summary>
[Collection("Catalog Integration")]
public sealed class CatalogFlowsTests : IAsyncLifetime
{
    private readonly CatalogWebAppFactory _factory;
    private readonly HttpClient _client;

    public CatalogFlowsTests(CatalogWebAppFactory factory)
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
    public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_then_get_category_round_trips()
    {
        var name = $"Cat-{Guid.NewGuid():N}";
        var createResp = await _client.PostAsJsonAsync("/api/categories",
            new { name, description = "x" });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var listResp = await _client.GetFromJsonAsync<CategoryDto[]>("/api/categories");
        listResp.Should().NotBeNull();
        listResp!.Should().Contain(c => c.Name == name);
    }

    [Fact]
    public async Task Create_then_list_product_round_trips()
    {
        var categoryId = await CreateCategoryAsync();
        var productId = await CreateProductAsync(categoryId, initialStock: 5);

        var get = await _client.GetFromJsonAsync<ProductDto>($"/api/products/{productId}");
        get.Should().NotBeNull();
        get!.StockQuantity.Should().Be(5);
        get.IsListed.Should().BeTrue();
        get.CategoryId.Should().Be(categoryId);
        get.CategoryName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Get_missing_product_returns_404()
    {
        var resp = await _client.GetAsync($"/api/products/{Guid.Empty}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_product_with_unknown_category_returns_404()
    {
        // Use a random Guid (not Guid.Empty) so the FluentValidation rule
        // `NotEqual(Guid.Empty)` doesn't short-circuit with a 400 before the
        // handler's not-found check runs.
        var resp = await _client.PostAsJsonAsync("/api/products", new
        {
            name = "Orphan",
            description = "x",
            unitPrice = 1m,
            categoryId = Guid.NewGuid(),
            initialStock = 0,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reserve_stock_decrements_and_publishes_event()
    {
        var categoryId = await CreateCategoryAsync();
        var productId = await CreateProductAsync(categoryId, initialStock: 10);

        var orderId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();

        var reserveResp = await _client.PostAsJsonAsync($"/api/products/{productId}/reserve",
            new
            {
                quantity = 3,
                orderId,
                sagaId,
                userId = "u1",
                totalAmount = 30m,
                currency = "USD",
                customerEmail = "u1@example.com",
                idempotencyKey = "key-1",
            });
        reserveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Stock decremented atomically.
        var get = await _client.GetFromJsonAsync<ProductDto>($"/api/products/{productId}");
        get!.StockQuantity.Should().Be(7);

        // StockReservedEvent published via the (replaced) in-memory transport.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var published = await harness.Published.Any<StockReservedEvent>();
        published.Should().BeTrue("the handler must publish StockReservedEvent through the outbox");

        var evt = harness.Published
            .Select<StockReservedEvent>()
            .FirstOrDefault(p => p.Context.Message.OrderId == orderId);
        evt.Should().NotBeNull();
        evt!.Context.Message.SagaId.Should().Be(sagaId);
        evt.Context.Message.Items.Should().ContainSingle(i => i.ProductId == productId && i.Quantity == 3);
        evt.Context.Message.Items.Single().RemainingStock.Should().Be(7);
        evt.Context.Message.OrderLineItems.Should().ContainSingle(i => i.ProductId == productId);
    }

    [Fact]
    public async Task ReserveStock_sets_IsInStock_false_when_exact_quantity_reserved()
    {
        var categoryId = await CreateCategoryAsync();
        var productId = await CreateProductAsync(categoryId, initialStock: 5);

        var orderId = Guid.NewGuid();
        var reserveResp = await _client.PostAsJsonAsync($"/api/products/{productId}/reserve",
            new
            {
                quantity = 5,
                orderId,
                sagaId = Guid.NewGuid(),
                userId = "u1",
                totalAmount = 50m,
                currency = "USD",
                customerEmail = "u1@example.com",
                idempotencyKey = "key-exact-" + Guid.NewGuid().ToString("N"),
            });
        reserveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.GetFromJsonAsync<ProductDto>($"/api/products/{productId}");
        get!.StockQuantity.Should().Be(0);
        get.IsInStock.Should().BeFalse("reserving the exact stock quantity must set IsInStock to false");
    }

    [Fact]
    public async Task Reserve_more_than_stock_returns_409_and_does_not_publish()
    {
        var categoryId = await CreateCategoryAsync();
        var productId = await CreateProductAsync(categoryId, initialStock: 2);

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var publishedBefore = harness.Published.Select<StockReservedEvent>().Count();

        var resp = await _client.PostAsJsonAsync($"/api/products/{productId}/reserve",
            new
            {
                quantity = 100,
                orderId = Guid.NewGuid(),
                sagaId = Guid.NewGuid(),
                userId = "u1",
                totalAmount = 100m,
                currency = "USD",
                customerEmail = "u1@example.com",
            });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var get = await _client.GetFromJsonAsync<ProductDto>($"/api/products/{productId}");
        get!.StockQuantity.Should().Be(2, "failed reservation must not decrement stock");

        // No new StockReservedEvent: outbox publish was rolled back with the
        // failed SaveChanges (no row to deliver).
        harness.Published.Select<StockReservedEvent>().Count().Should().Be(publishedBefore);
    }

    private async Task<Guid> CreateCategoryAsync()
    {
        var name = $"Cat-{Guid.NewGuid():N}";
        var resp = await _client.PostAsJsonAsync("/api/categories",
            new { name, description = "x" });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateProductAsync(Guid categoryId, int initialStock)
    {
        var resp = await _client.PostAsJsonAsync("/api/products", new
        {
            name = $"P-{Guid.NewGuid():N}",
            description = "x",
            unitPrice = 9.99m,
            categoryId,
            initialStock,
        });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Guid>();
    }

    private sealed record ProductDto(
        Guid Id, string Name, string Description, decimal UnitPrice,
        int StockQuantity, bool IsInStock, bool IsListed,
        Guid CategoryId, string? CategoryName);

    private sealed record CategoryDto(Guid Id, string Name, string? Description);
}
