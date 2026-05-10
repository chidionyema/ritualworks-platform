using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Haworks.Audit.Application.Export;
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
public class ExportJobTests : IClassFixture<AuditWebAppFactory>
{
    private readonly AuditWebAppFactory _factory;

    public ExportJobTests(AuditWebAppFactory factory)
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
    public async Task SubmitExport_ShouldReturnAccepted()
    {
        // Arrange
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await db.Database.MigrateAsync();
        }

        var request = new AuditExportRequest(
            EntityId: null,
            EntityType: null,
            EventType: null,
            From: DateTimeOffset.UtcNow.AddDays(-1),
            To: DateTimeOffset.UtcNow);

        var client = CreateClient();

        // Act
        var response = await client.PostAsJsonAsync("/audit/export", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var result = await response.Content.ReadFromJsonAsync<ExportResult>();
        result.Should().NotBeNull();
        result!.JobId.Should().NotBeEmpty();
    }

    private record ExportResult(Guid JobId, string Status);

    private class AuditTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "audit-admin"),
                new Claim(ClaimTypes.Role, "audit-reader")
            };
            var identity = new ClaimsIdentity(claims, TestAuthenticationHandler.SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, TestAuthenticationHandler.SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
