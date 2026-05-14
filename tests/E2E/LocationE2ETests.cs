using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class LocationE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public LocationE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _apiContext = await _fixture.CreateApiContextAsync();
    }

    public async Task DisposeAsync()
    {
        await _apiContext.DisposeAsync();
    }

    [Fact]
    public async Task CreateLocation_ThroughBff_Succeeds()
    {
        _output.WriteLine("--- STARTING LOCATION E2E ---");

        // 1. Create Location via BFF
        var locationCommand = new
        {
            street = "Buckingham Palace",
            city = "London",
            postcode = "SW1A 1AA",
            country = "United Kingdom",
            latitude = 51.5014,
            longitude = -0.1419
        };

        var response = await _apiContext.PostAsync("/api/locations", new APIRequestContextOptions
        {
            DataObject = locationCommand
        });

        // Assert
        response.Status.Should().Be(200);
        var locationId = await response.TextAsync();
        locationId.Should().NotBeNullOrEmpty();
        
        _output.WriteLine($"Created location with ID: {locationId}");
    }
}
