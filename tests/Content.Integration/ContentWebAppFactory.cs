using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.Minio;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Content.Infrastructure.Persistence;
using Xunit;

namespace Haworks.Content.Integration;

public sealed class ContentWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("content")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _minio.StartAsync();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__content", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Minio__Endpoint", _minio.GetConnectionString());
        Environment.SetEnvironmentVariable("Minio__AccessKey", _minio.GetAccessKey());
        Environment.SetEnvironmentVariable("Minio__SecretKey", _minio.GetSecretKey());
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:content"] = _postgres.GetConnectionString(),
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["Minio:Endpoint"] = _minio.GetConnectionString(),
                ["Minio:AccessKey"] = _minio.GetAccessKey(),
                ["Minio:SecretKey"] = _minio.GetSecretKey(),
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // [Authorize]-decorated endpoints need an authentication scheme.
            // Stamp the shared no-op test scheme as default so the controller's
            // ContentUploader policy passes (handler grants the role).
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
        await db.Database.MigrateAsync();
    }
}
