using FluentAssertions;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Search.Application.Catalog;
using Haworks.Search.Infrastructure.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// Black-box coverage of CatalogProductsApiClient against a WireMock stub.
/// Verifies (a) deserialization of catalog's actual response shape,
/// (b) retry-then-fail behaviour for transient 5xx, (c) hard-fail on 404,
/// (d) skip/take query string for the offset-paginated list endpoint.
/// </summary>
public sealed class CatalogProductsApiTests : IDisposable
{
    private readonly WireMockServer _wiremock;
    private readonly ServiceProvider _services;
    private readonly ICatalogProductsApi _client;

    public CatalogProductsApiTests()
    {
        _wiremock = WireMockServer.Start();

        var collection = new ServiceCollection();
        collection.AddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();
        collection.AddSingleton(NullLoggerFactory.Instance);
        collection.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        collection.AddHttpClient<ICatalogProductsApi, CatalogProductsApiClient>(c =>
        {
            c.BaseAddress = new Uri(_wiremock.Url!);
            c.Timeout = TimeSpan.FromSeconds(2);
        });

        _services = collection.BuildServiceProvider();
        _client = _services.GetRequiredService<ICatalogProductsApi>();
    }

    public void Dispose()
    {
        _wiremock.Stop();
        _wiremock.Dispose();
        _services.Dispose();
    }

    [Fact]
    public async Task GetProductAsync_returns_dto_when_catalog_returns_200()
    {
        var id = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        _wiremock
            .Given(Request.Create().WithPath($"/api/products/{id}").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    id,
                    name = "Pen",
                    description = "Blue ink pen",
                    unitPrice = 1.99m,
                    stockQuantity = 5,
                    isInStock = true,
                    isListed = true,
                    categoryId,
                    categoryName = "Stationery",
                })));

        var dto = await _client.GetProductAsync(id, default);

        dto.Should().NotBeNull();
        dto.Id.Should().Be(id);
        dto.Name.Should().Be("Pen");
        dto.CategoryId.Should().Be(categoryId);
        dto.CategoryName.Should().Be("Stationery");
    }

    [Fact]
    public async Task GetProductAsync_throws_after_retry_when_catalog_5xx()
    {
        var id = Guid.NewGuid();
        _wiremock
            .Given(Request.Create().WithPath($"/api/products/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var act = async () => await _client.GetProductAsync(id, default);

        // The combined ForExternalApi policy retries before surfacing the
        // failure — we only assert the eventual exception, not the count.
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetProductAsync_throws_404_for_unknown_product()
    {
        var id = Guid.NewGuid();
        _wiremock
            .Given(Request.Create().WithPath($"/api/products/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var act = async () => await _client.GetProductAsync(id, default);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListProductsAsync_paginates_via_skip_take()
    {
        // Page 1: skip=0&take=100, returns 100 items + Total=150.
        _wiremock
            .Given(Request.Create().WithPath("/api/products")
                .WithParam("skip", "0").WithParam("take", "100").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(0, 100).Select(_ => MakeListItem()).ToArray(),
                    total = 150,
                    skip = 0,
                    take = 100,
                })));

        // Page 2: skip=100&take=100, returns 50 items.
        _wiremock
            .Given(Request.Create().WithPath("/api/products")
                .WithParam("skip", "100").WithParam("take", "100").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(0, 50).Select(_ => MakeListItem()).ToArray(),
                    total = 150,
                    skip = 100,
                    take = 100,
                })));

        var page1 = await _client.ListProductsAsync(0, 100, null, default);
        var page2 = await _client.ListProductsAsync(100, 100, null, default);

        page1.Items.Should().HaveCount(100);
        page1.Total.Should().Be(150);
        page1.Skip.Should().Be(0);

        page2.Items.Should().HaveCount(50);
        page2.Skip.Should().Be(100);

        // Items should round-trip with categoryName=null (matches catalog reality).
        page1.Items.Should().AllSatisfy(item => item.CategoryName.Should().BeNull());
    }

    private static object MakeListItem()
    {
        var id = Guid.NewGuid();
        return new
        {
            id,
            name = "X",
            description = "Y",
            unitPrice = 1m,
            stockQuantity = 1,
            isInStock = true,
            isListed = true,
            categoryId = Guid.NewGuid(),
            categoryName = (string?)null,
        };
    }
}
