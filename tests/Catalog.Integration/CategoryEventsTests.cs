using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.Testing;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.DTOs;
using Haworks.Contracts.Catalog;

namespace Haworks.Catalog.Integration;

/// <summary>
/// Coverage for catalog → search-svc event contract: renaming a category
/// must publish CategoryUpdatedEvent through the EF outbox so search-svc
/// can re-denormalize categoryName on indexed products.
/// </summary>
public sealed class CategoryEventsTests : IClassFixture<CatalogWebAppFactory>, IAsyncLifetime
{
    private readonly CatalogWebAppFactory _factory;
    private readonly HttpClient _client;

    public CategoryEventsTests(CatalogWebAppFactory factory)
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
    public async Task UpdateCategory_publishes_CategoryUpdatedEvent()
    {
        // Arrange — create a category through the existing HTTP endpoint
        var originalName = $"Cat-{Guid.NewGuid():N}";
        var createResp = await _client.PostAsJsonAsync("/api/categories",
            new { name = originalName, description = "test" });
        createResp.EnsureSuccessStatusCode();

        var listing = await _client.GetFromJsonAsync<CategoryDto[]>("/api/categories");
        listing.Should().NotBeNull();
        var created = listing!.Single(c => c.Name == originalName);

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var publishedBefore = harness.Published.Select<CategoryUpdatedEvent>().Count();

        // Act — rename via MediatR (catalog has no HTTP PUT for categories yet;
        // exercising the handler directly is the convention for command-only
        // surfaces, and tests the same publish path the future PUT will use).
        var newName = $"Cat-renamed-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var result = await mediator.Send(new UpdateCategoryCommand(created.Id, newName));
        result.IsSuccess.Should().BeTrue();

        // Assert — exactly one new CategoryUpdatedEvent for this category
        harness.Published.Select<CategoryUpdatedEvent>().Count()
            .Should().Be(publishedBefore + 1, "the handler must publish CategoryUpdatedEvent through the outbox");

        var evt = harness.Published.Select<CategoryUpdatedEvent>()
            .FirstOrDefault(p => p.Context.Message.CategoryId == created.Id);
        evt.Should().NotBeNull();
        evt!.Context.Message.Name.Should().Be(newName);
    }
}
