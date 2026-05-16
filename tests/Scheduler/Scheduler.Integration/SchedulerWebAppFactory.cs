using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Hangfire;

namespace Haworks.Scheduler.Integration;

public class SchedulerWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string ConnString { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnString = await SharedTestPostgres.CreateDatabaseAsync("scheduler");

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__scheduler", ConnString);
        Environment.SetEnvironmentVariable("RabbitMq__Host", "localhost");

        // AddPlatformAuthentication requires JwksOptions to be present at host startup.
        // Values are test-grade placeholders; the real JWT pipeline is bypassed by TestAuthenticationHandler.
        JwtTestDefaults.SetTestEnvironmentVariables();
    }

    public new Task DisposeAsync() => Task.CompletedTask;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:scheduler"] = ConnString,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var mockBackgroundJobClient = new Mock<IBackgroundJobClient>();
            services.AddSingleton(mockBackgroundJobClient.Object);
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }
}
