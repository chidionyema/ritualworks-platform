using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Haworks.BuildingBlocks.Testing;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Haworks.Payouts.Infrastructure.Persistence;
using Haworks.Payouts.Application.Common.Interfaces;

namespace Haworks.Payouts.Integration;

public class PayoutsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private DatabaseResetter? _resetter;
    private string ConnString { get; set; } = string.Empty;

    public async Task InitializeAsync() 
    { 
        ConnString = await SharedTestPostgres.CreateDatabaseAsync("payouts"); 
        _resetter = new DatabaseResetter(ConnString);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test"); 
        Environment.SetEnvironmentVariable("ConnectionStrings__payouts", ConnString); 
        Environment.SetEnvironmentVariable("Stripe__SecretKey", "sk_test_dummy"); 
        Environment.SetEnvironmentVariable("RabbitMq__Host", "localhost"); 
        JwtTestDefaults.SetTestEnvironmentVariables(); 
    }

    public new Task DisposeAsync() => Task.CompletedTask;

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PayoutsDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS payouts;");
        await db.Database.EnsureCreatedAsync();
    }

    public Task ResetDatabaseAsync() => _resetter!.ResetAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
 { builder.UseEnvironment("Test"); builder.ConfigureAppConfiguration((_, config) => { config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:payouts"] = ConnString, ["Stripe:SecretKey"] = "sk_test_dummy" }); }); builder.ConfigureTestServices(services => { var mockGateway = new Mock<IPayoutGateway>(); mockGateway.Setup(x => x.InitiatePayoutAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(("tr_test", Haworks.Payouts.Domain.Enums.PayoutStatus.Succeeded)); services.AddSingleton(mockGateway.Object); services.AddMassTransitTestHarness(); services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth(); }); }
}
