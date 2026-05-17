using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Merchant.Application.Merchants.DTOs;
using Haworks.Contracts.Merchant;

namespace Haworks.Merchant.Integration;

[Collection("Merchant Integration")]
public sealed class MerchantFlowsTests : IAsyncLifetime
{
    private readonly MerchantWebAppFactory _factory;
    private readonly HttpClient _client;

    public MerchantFlowsTests(MerchantWebAppFactory factory)
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
    public async Task Create_then_get_merchant_round_trips()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var name = "Test Merchant";
        var slug = $"test-merchant-{Guid.NewGuid():N}";
        var command = new { OwnerId = ownerId, Name = name, Slug = slug };

        // Act
        var createResp = await _client.PostAsJsonAsync("/api/merchants", command);
        
        // Assert
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await createResp.Content.ReadFromJsonAsync<CreateResult>();
        createResult.Should().NotBeNull();
        createResult!.MerchantId.Should().NotBeEmpty();

        // Get by ID
        var getResp = await _client.GetAsync($"/api/merchants/{createResult.MerchantId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var merchant = await getResp.Content.ReadFromJsonAsync<MerchantDto>();
        merchant.Should().NotBeNull();
        merchant!.Name.Should().Be(name);
        merchant.Slug.Should().Be(slug);
        merchant.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task Create_merchant_publishes_MerchantCreatedEvent()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var name = "Event Test Merchant";
        var slug = $"event-test-{Guid.NewGuid():N}";
        var command = new { OwnerId = ownerId, Name = name, Slug = slug };
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        // Act
        await _client.PostAsJsonAsync("/api/merchants", command);

        // Assert
        (await harness.Published.Any<MerchantCreatedEvent>(x => string.Equals(x.Context.Message.Slug, slug, StringComparison.Ordinal))).Should().BeTrue();
    }

    private record CreateResult(Guid MerchantId);
}
