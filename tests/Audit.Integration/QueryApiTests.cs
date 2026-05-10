using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Haworks.Audit.Api.Models;
using Haworks.Audit.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Testing.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Haworks.Audit.Integration;

[Collection("AuditIntegration")]
public class QueryApiTests : IClassFixture<AuditWebAppFactory>
{
    private readonly AuditWebAppFactory _factory;

    public QueryApiTests(AuditWebAppFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options => 
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, AuditTestAuthHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        }).CreateClient();
    }

    [Fact]
    public async Task ListEvents_ShouldReturnEvents()
    {
        // Arrange
        var factoryWithSuppression = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<AuditDbContext>(options =>
                {
                    options.ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });
            });
        });

        using (var scope = factoryWithSuppression.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await db.Database.MigrateAsync();

            // Create base table if it doesn't exist (L1.B might not have run)
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS audit_events (
                    id uuid NOT NULL,
                    occurred_at timestamptz NOT NULL,
                    received_at timestamptz NOT NULL,
                    event_type text NOT NULL,
                    entity_type text NOT NULL,
                    entity_id text NOT NULL,
                    actor_id text,
                    actor_type text,
                    correlation_id text,
                    payload jsonb NOT NULL,
                    metadata jsonb NOT NULL,
                    PRIMARY KEY (id, occurred_at)
                ) PARTITION BY RANGE (occurred_at);
            ");
        }

        var client = CreateClient();

        // Act
        var response = await client.GetAsync("/audit/events");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<AuditPageResponse<AuditEventDto>>();
        result.Should().NotBeNull();
    }

    private class AuditTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "audit-reader")
            };
            var identity = new ClaimsIdentity(claims, TestAuthenticationHandler.SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, TestAuthenticationHandler.SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
