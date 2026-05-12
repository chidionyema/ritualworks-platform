using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Location.Application.Commands;
using Xunit;

namespace Haworks.Location.Integration;

[Collection("Location Integration")]
public class LocationFlowsTests(LocationWebAppFactory factory) : IClassFixture<LocationWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateAddress_ShouldReturnIdAndPersistData()
    {
        // Arrange
        var command = new CreateAddressCommand
        {
            Street = "10 Downing St",
            City = "London",
            Postcode = "SW1A 2AA",
            Country = "United Kingdom",
            Latitude = 51.5033,
            Longitude = -0.1276
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/addresses", command);

        // Assert
        response.EnsureSuccessStatusCode();
        var id = await response.Content.ReadFromJsonAsync<Guid>();
        id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetNearby_ShouldReturnCorrectLocations()
    {
        // 1. Create two locations
        var london = new CreateAddressCommand
        {
            Street = "Downing St",
            City = "London",
            Postcode = "SW1A 2AA",
            Country = "UK",
            Latitude = 51.5033,
            Longitude = -0.1276
        };

        var bristol = new CreateAddressCommand
        {
            Street = "Bristol St",
            City = "Bristol",
            Postcode = "BS1 1AA",
            Country = "UK",
            Latitude = 51.4545,
            Longitude = -2.5879
        };

        await _client.PostAsJsonAsync("/api/addresses", london);
        await _client.PostAsJsonAsync("/api/addresses", bristol);

        // 2. Search near London (5km radius)
        var response = await _client.GetAsync("/api/addresses/nearby?lat=51.5033&lon=-0.1276&radiusMeters=5000");

        // Assert
        response.EnsureSuccessStatusCode();
        var results = await response.Content.ReadFromJsonAsync<List<dynamic>>();
        results.Should().NotBeNull();
        // london is approx 0 distance, bristol is ~170km away
        results!.Count.Should().Be(1);
    }
}

[CollectionDefinition("Location Integration")]
public class LocationIntegrationFixture : ICollectionFixture<LocationWebAppFactory> { }
