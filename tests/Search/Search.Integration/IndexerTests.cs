using FluentAssertions;
using Haworks.Contracts.Catalog;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using WireMock.RequestBuilders;
using Xunit;
using WireMockResponse = WireMock.ResponseBuilders.Response;

namespace Haworks.Search.Integration;

/// <summary>
/// End-to-end indexer coverage: publish a Catalog event through the test
/// harness, observe Elasticsearch state. Catalog HTTP enrichment is
/// served by WireMock (the SearchWebAppFactory wires Catalog:BaseAddress
/// to the WireMock URL).
/// </summary>
[Collection("Search Integration")]
public sealed class IndexerTests : IAsyncLifetime
{
    private readonly SearchWebAppFactory _factory;

    public IndexerTests(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Force the host to build (so EnsureSettings runs against the test
        // Meili index) and start the test harness.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        // EnsureSettings happens on Program startup, but Test env still
        // needs the index to exist before we publish events.
        using var scope = _factory.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        await index.EnsureSettingsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ProductCacheInvalidated_with_Reason_updated_upserts_document()
    {
        var productId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        StubProduct(productId, categoryId, "Wireless Headphones", "Audio");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Bus.Publish(new ProductCacheInvalidatedEvent
        {
            ProductId = productId,
            Reason = "updated",
            NewVersion = 5,
        });

        await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            var doc = await index.GetAsync(productId.ToString("N"));
            return doc?.Name == "Wireless Headphones";
        }, TimeSpan.FromSeconds(20));

        using var verifyScope = _factory.Services.CreateScope();
        var idx = verifyScope.ServiceProvider.GetRequiredService<ISearchIndex>();
        var stored = await idx.GetAsync(productId.ToString("N"));
        stored.Should().NotBeNull();
        stored!.CategoryName.Should().Be("Audio");
        stored.SourceVersion.Should().Be(5);
    }

    [Fact]
    public async Task ProductCacheInvalidated_with_Reason_deleted_removes_document()
    {
        var productId = Guid.NewGuid();

        // Seed the index directly (skip catalog roundtrip).
        using (var scope = _factory.Services.CreateScope())
        {
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            await index.UpsertAsync(new[] { MakeDoc(productId, "X", "Y", 9) });
        }

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Bus.Publish(new ProductCacheInvalidatedEvent
        {
            ProductId = productId,
            Reason = "deleted",
        });

        await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            return (await index.GetAsync(productId.ToString("N"))) is null;
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ProductCacheInvalidated_with_lower_SourceVersion_is_a_noop()
    {
        var productId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        StubProduct(productId, categoryId, "OriginalName", "OriginalCat");

        // Seed the index with version 10.
        using (var scope = _factory.Services.CreateScope())
        {
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            await index.UpsertAsync(new[] { MakeDoc(productId, "OriginalName", "OriginalCat", sourceVersion: 10) });
        }

        // Publish event with version 5 (older).
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Bus.Publish(new ProductCacheInvalidatedEvent
        {
            ProductId = productId,
            Reason = "updated",
            NewVersion = 5,
        });

        // Wait for the consumer to process (it should observe the version
        // guard and skip). Hard to polling-assert "did nothing" — wait for
        // the consume to register, then assert document state unchanged.
        await harness.Consumed.Any<ProductCacheInvalidatedEvent>(
            new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

        using var scope2 = _factory.Services.CreateScope();
        var idx = scope2.ServiceProvider.GetRequiredService<ISearchIndex>();
        var doc = await idx.GetAsync(productId.ToString("N"));
        doc.Should().NotBeNull();
        doc!.SourceVersion.Should().Be(10, "the version guard must reject older events");
        doc.Name.Should().Be("OriginalName");
    }

    [Fact]
    public async Task CategoryUpdated_renames_category_for_all_products()
    {
        var categoryId = Guid.NewGuid();
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            await index.UpsertAsync(new[]
            {
                MakeDoc(p1, "P1", "OldCat", 1, categoryId),
                MakeDoc(p2, "P2", "OldCat", 1, categoryId),
                MakeDoc(p3, "P3", "OldCat", 1, categoryId),
            });
        }

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Bus.Publish(new CategoryUpdatedEvent
        {
            CategoryId = categoryId,
            Name = "NewCat",
        });

        await PollUntilAsync(async () =>
        {
            using var scope = _factory.Services.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();
            var d1 = await index.GetAsync(p1.ToString("N"));
            var d2 = await index.GetAsync(p2.ToString("N"));
            var d3 = await index.GetAsync(p3.ToString("N"));
            return d1?.CategoryName == "NewCat"
                && d2?.CategoryName == "NewCat"
                && d3?.CategoryName == "NewCat";
        }, TimeSpan.FromSeconds(20));
    }

    private void StubProduct(Guid id, Guid categoryId, string name, string categoryName)
    {
        _factory.Catalog
            .Given(Request.Create().WithPath($"/api/products/{id}").UsingGet())
            .RespondWith(WireMockResponse.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new
                {
                    id,
                    name,
                    description = "Test product",
                    unitPrice = 99.99m,
                    stockQuantity = 10,
                    isInStock = true,
                    isListed = true,
                    categoryId,
                    categoryName,
                })));
    }

    private static ProductSearchDocument MakeDoc(Guid id, string name, string categoryName, long sourceVersion, Guid? categoryId = null)
        => new()
        {
            ProductIdKey = id.ToString("N"),
            ProductId = id.ToString(),
            Name = name,
            Description = "seed",
            CategoryId = (categoryId ?? Guid.NewGuid()).ToString(),
            CategoryName = categoryName,
            UnitPrice = 1m,
            IsInStock = true,
            IsListed = true,
            SourceVersion = sourceVersion,
            IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

    private static async Task PollUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Predicate not satisfied within {timeout}");
    }
}
