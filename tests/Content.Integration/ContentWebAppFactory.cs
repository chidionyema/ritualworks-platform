using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Content.Infrastructure.Persistence;
using Xunit;

namespace Haworks.Content.Integration;

/// <summary>
/// WebApplicationFactory for content-svc integration tests.
///
/// Storage is LocalStack S3 (the same emulator used by docker-compose +
/// Aspire). Production uses Fly Tigris — the AWS-SDK-based StorageOptions
/// targets both transparently. Bucket is pre-created via the AWS SDK once
/// LocalStack is healthy so the first upload doesn't 404.
/// </summary>
public sealed class ContentWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string BucketName = "content-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("content")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly IContainer _localstack = new ContainerBuilder()
        .WithImage("localstack/localstack:3")
        .WithEnvironment("SERVICES", "s3")
        .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
        .WithPortBinding(4566, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPath("/_localstack/health").ForPort(4566)))
        .WithReuse(true)
        .Build();

    private string _localstackUrl = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _localstack.StartAsync();

        _localstackUrl = $"http://{_localstack.Hostname}:{_localstack.GetMappedPublicPort(4566)}";

        // Env vars must be set BEFORE WebApplicationFactory builds the host —
        // top-level Program.cs runs to construct the WebApplicationBuilder
        // before WAF's ConfigureAppConfiguration hook fires.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__content", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Storage__ServiceUrl",     _localstackUrl);
        Environment.SetEnvironmentVariable("Storage__AccessKey",      "test");
        Environment.SetEnvironmentVariable("Storage__SecretKey",      "test");
        Environment.SetEnvironmentVariable("Storage__BucketName",     BucketName);
        Environment.SetEnvironmentVariable("Storage__Region",         "us-east-1");
        Environment.SetEnvironmentVariable("Storage__ForcePathStyle", "true");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        // Pre-create the bucket so the first upload in any test succeeds.
        // Done via the AWS SDK directly rather than spawning aws-cli inside
        // a sidecar — keeps the fixture self-contained.
        await EnsureBucketAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        // _localstack uses .WithReuse(true) — Testcontainers keeps the container
        // alive across runs to amortize the ~3s startup cost. Skipping explicit
        // DisposeAsync on the localstack container is intentional.
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
                ["Storage:ServiceUrl"]     = _localstackUrl,
                ["Storage:AccessKey"]      = "test",
                ["Storage:SecretKey"]      = "test",
                ["Storage:BucketName"]     = BucketName,
                ["Storage:Region"]         = "us-east-1",
                ["Storage:ForcePathStyle"] = "true",
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

    private async Task EnsureBucketAsync()
    {
        var s3Config = new AmazonS3Config
        {
            ServiceURL = _localstackUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        using var s3 = new AmazonS3Client("test", "test", s3Config);
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Fixture re-use across runs (.WithReuse(true)) means the bucket
            // may persist from a prior run — that's fine.
        }
    }
}
