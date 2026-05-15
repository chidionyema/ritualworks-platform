using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Haworks.Audit.Application.Extraction;
using Haworks.Audit.Application.Redaction;
using Haworks.Audit.Infrastructure.Persistence;
using MassTransit;
using System.Text.Json;

namespace Haworks.Audit.Integration;

public class AuditWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;
    public string RabbitMqConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("audit");
        RabbitMqConnectionString = await SharedTestRabbitMq.GetConnectionStringAsync();
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__audit", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", RabbitMqConnectionString);
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        // Force the host to build so Services are available for migration
        _ = Services;
        await EnsureSchemaAsync();
    }

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS audit;");
            await db.Database.EnsureCreatedAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

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
                ["ConnectionStrings:audit"] = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = RabbitMqConnectionString,
                ["Vault:Enabled"] = "false",
            });
        });

        // Add stubs for L1.A if they are not registered yet
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(typeof(IAuditExtractor<>), typeof(TestStubExtractor<>));
            services.AddSingleton<ISecretRedactor, TestStubRedactor>();
        });
    }
}

public class TestStubExtractor<T> : IAuditExtractor<T> where T : class, Haworks.Contracts.IDomainEvent
{
    public AuditRow Extract(T evt, ConsumeContext<T> ctx)
    {
        // Minimal extraction logic for L1.B integration testing
        var json = JsonSerializer.SerializeToElement(evt);
        string entityId = "";
        string entityType = "unknown";

        if (json.TryGetProperty("OrderId", out var orderIdProp))
        {
            entityId = orderIdProp.GetGuid().ToString();
            entityType = "order";
        }
        else if (json.TryGetProperty("PaymentId", out var paymentIdProp))
        {
            entityId = paymentIdProp.GetGuid().ToString();
            entityType = "payment";
        }

        return new AuditRow(
            DateTimeOffset.UtcNow,
            typeof(T).Name,
            entityType,
            entityId,
            "test-actor",
            "user",
            ctx.CorrelationId?.ToString(),
            json,
            JsonSerializer.SerializeToElement(new Dictionary<string, object>())
        );
    }
}

public class TestStubRedactor : ISecretRedactor
{
    public JsonElement Redact(JsonElement input) => input;
}
