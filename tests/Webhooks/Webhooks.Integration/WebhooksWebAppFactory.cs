using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;
using Haworks.BuildingBlocks.Testing;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Haworks.Webhooks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Haworks.Webhooks.Integration;

public class WebhooksWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private DatabaseResetter? _resetter;
    public string ConnectionString { get; private set; } = string.Empty;
    public string RabbitMqConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("webhooks");
        RabbitMqConnectionString = "amqp://guest:guest@localhost:5672/";
        _resetter = new DatabaseResetter(ConnectionString);

        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__webhooks", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", RabbitMqConnectionString);
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
        Environment.SetEnvironmentVariable("Kafka__Enabled", "false");
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", "localhost:9092");
        Environment.SetEnvironmentVariable("Kafka__GroupId", "webhooks-svc-cdc-test");

        // Force host build so Services are available, then apply schema
        _ = Services;
        await EnsureSchemaAsync();
    }

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WebhooksDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS webhooks;");
        
        var creator = db.Database.GetService<IRelationalDatabaseCreator>();
        try { await creator.CreateTablesAsync(); }
        catch (Npgsql.PostgresException ex) when (string.Equals(ex.SqlState, "42P07", StringComparison.Ordinal)) { /* tables already exist */ }
    }

    public Task ResetDatabaseAsync() => _resetter!.ResetAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:webhooks"] = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = RabbitMqConnectionString,
                ["Vault:Enabled"] = "false",
                ["Kafka:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var httpClient = new HttpClient();
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(httpClient);
            
            services.AddSingleton(mockFactory.Object);
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }
}
