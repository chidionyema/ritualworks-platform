using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Localization.Api.Domain;
using Haworks.Localization.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MassTransit.Testing;

namespace Haworks.Localization.Integration;

[Collection(LocalizationIntegrationCollection.Name)]
public class LocalizationFlowsTests : IAsyncLifetime
{
    private readonly LocalizationWebAppFactory _factory;
    private readonly HttpClient _client;

    public LocalizationFlowsTests(LocalizationWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetTranslation_ShouldReturnSuccess_WhenKeyExists()
    {
        // Arrange
        var key = "welcome_message";
        var values = new Dictionary<string, string>
        {
            { "en-US", "Welcome!" },
            { "fr-FR", "Bienvenue !" }
        };
        
        await SeedTranslation(key, values);

        // Act
        var response = await _client.GetAsync($"/api/translations/{key}?locale=en-US");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Welcome!");
    }

    [Fact]
    public async Task GetTranslation_ShouldReturnNotFound_WhenKeyDoesNotExist()
    {
        // Arrange
        var key = "non_existent_key";

        // Act
        var response = await _client.GetAsync($"/api/translations/{key}?locale=en-US");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTranslation_ShouldFallback_WhenLocaleDoesNotExistButDefaultDoes()
    {
        // Arrange
        var key = "fallback_test";
        var values = new Dictionary<string, string>
        {
            { "en-US", "Default Welcome" }
        };
        
        await SeedTranslation(key, values);

        // Act
        var response = await _client.GetAsync($"/api/translations/{key}?locale=de-DE");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Default Welcome");
    }

    private async Task SeedTranslation(string key, Dictionary<string, string> values)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalizationDbContext>();
        
        var existing = await db.Translations.FindAsync(key);
        if (existing != null)
        {
            db.Translations.Remove(existing);
            await db.SaveChangesAsync();
        }

        db.Translations.Add(new Translation(key, values));
        await db.SaveChangesAsync();
    }
}
