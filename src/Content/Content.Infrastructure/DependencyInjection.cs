using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Infrastructure.ExternalServices.Storage;
using Haworks.Content.Infrastructure.ExternalServices.Validation;
using Haworks.Content.Infrastructure.Options;
using Haworks.Content.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Content.Domain.Interfaces;
using Minio;

namespace Haworks.Content.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Connection string resolution order matches Identity/Catalog/Payments:
        //   1. Aspire-injected ConnectionStrings__content
        //   2. ConnectionStrings:DefaultConnection (override / standalone runs)
        var connectionString = configuration.GetConnectionString("content")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No content database connection string. Expected 'ConnectionStrings:content' " +
                "(Aspire-injected) or 'ConnectionStrings:DefaultConnection'.");

        services.AddDbContext<ContentDbContext>((sp, options) => {
            options.UseNpgsql(connectionString);

            // Vault dynamic credentials interceptor — optional. Wired only when
            // services.AddVaultIntegration() is also present; otherwise the
            // plain Aspire-injected creds are used (test envs / dev mode).
            var interceptor = sp.GetService<DynamicCredentialsConnectionInterceptor>();
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });

        services.AddScoped<IContentRepository, ContentContextRepository>();
        services.AddScoped<IContentStorageService, ContentStorageService>();
        services.AddScoped<IFileSignatureValidator, FileSignatureValidator>();
        services.AddScoped<IVirusScanner, ClamAVScanner>();

        // S3-compatible object storage (MinIO SDK works against MinIO,
        // Fly Tigris, Cloudflare R2, AWS S3 — anything that speaks S3).
        // Only registered when ALL required MinIO fields are supplied;
        // partial config is treated as "not configured" so test fixtures
        // and dev environments without MinIO can boot. Content still boots
        // without it, but any upload fails fast at request time with a
        // clear DI-resolution error.
        var minioSection = configuration.GetSection(MinioOptions.SectionName);
        var hasFullMinioConfig =
            !string.IsNullOrWhiteSpace(minioSection["Endpoint"]) &&
            !string.IsNullOrWhiteSpace(minioSection["AccessKey"]) &&
            !string.IsNullOrWhiteSpace(minioSection["SecretKey"]) &&
            !string.IsNullOrWhiteSpace(minioSection["BucketName"]);

        if (hasFullMinioConfig)
        {
            services.AddOptions<MinioOptions>()
                .Bind(minioSection)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddSingleton<IMinioClient>(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
                return (IMinioClient)new MinioClient()
                    .WithEndpoint(opts.Endpoint)
                    .WithCredentials(opts.AccessKey, opts.SecretKey)
                    .WithSSL(opts.Secure)
                    .Build();
            });
        }

        return services;
    }
}
