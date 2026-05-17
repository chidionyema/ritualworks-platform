using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Haworks.FeatureFlags.Api.Application;
using Xunit;

namespace Haworks.FeatureFlags.Integration;

[Collection("FeatureFlags integration tests")]
public class FeatureFlagFlowsTests : IAsyncLifetime
{
    private readonly FeatureFlagsWebAppFactory _factory;
    private readonly HttpClient _client;

    public FeatureFlagFlowsTests(FeatureFlagsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpdateAndEvaluate_ShouldReflectChanges()
    {
        // 1. Update (create) a flag
        var updateCommand = new UpdateFlagCommand("test_flag", true, "Test Flag Description");
        var updateResponse = await _client.PostAsJsonAsync("/api/FeatureFlags/update", updateCommand);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Evaluate the flag
        // Note: The evaluation might fail initially if the cache hasn't processed the MassTransit event.
        // But the handler uses IFeatureFlagCache which is updated by the consumer.
        // In this integration test, we might need to wait for the consumer if we rely on the cache.
        // However, the UpdateFlagHandler publishes a message, and the FeatureFlagUpdatedConsumer updates the cache.
        
        // Let's wait a bit for the message to be processed if needed, or check if we can force a cache refresh.
        // Actually, the harness is in-memory, so it should be fast.
        
        await Task.Delay(500); // Simple wait for the consumer to run

        var evaluateResponse = await _client.GetAsync("/api/FeatureFlags/evaluate?flagName=test_flag&region=US");
        evaluateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await evaluateResponse.Content.ReadFromJsonAsync<bool>();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_UnknownFlag_ShouldReturnFalse()
    {
        var evaluateResponse = await _client.GetAsync("/api/FeatureFlags/evaluate?flagName=non_existent&region=US");
        evaluateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await evaluateResponse.Content.ReadFromJsonAsync<bool>();
        result.Should().BeFalse();
    }
}
