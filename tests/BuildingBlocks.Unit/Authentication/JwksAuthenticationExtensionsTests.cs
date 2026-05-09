using FluentAssertions;
using Haworks.BuildingBlocks.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Authentication;

public sealed class JwksAuthenticationExtensionsTests
{
    [Fact]
    public void AddJwksAuthentication_WithValidConfig_DoesNotThrow()
    {
        // Arrange — minimal configuration that satisfies the
        // [Required]/[Url] DataAnnotations on JwksOptions.
        var services = BuildServices();
        var configuration = BuildConfiguration();

        // Act
        var act = () => services.AddJwksAuthentication(configuration);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task AddJwksAuthentication_RegistersJwtBearerScheme()
    {
        // Arrange
        var services = BuildServices();
        var configuration = BuildConfiguration();

        services.AddJwksAuthentication(configuration);
        await using var provider = services.BuildServiceProvider();

        // Act
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemeProvider.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);

        // Assert
        scheme.Should().NotBeNull(
            "AddJwksAuthentication must register the JwtBearer scheme so downstream services can [Authorize] against it.");
        scheme!.Name.Should().Be(JwtBearerDefaults.AuthenticationScheme);
        scheme.HandlerType.Should().Be<JwtBearerHandler>();
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        return services;
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Jwks:JwksUri"] = "https://identity.local/.well-known/jwks.json",
                ["Authentication:Jwks:Issuer"] = "https://identity.local",
                ["Authentication:Jwks:Audience"] = "haworks.tests",
            })
            .Build();
}
