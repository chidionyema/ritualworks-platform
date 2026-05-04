using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Infrastructure.ExternalServices.Storage;
using Haworks.Content.Infrastructure.ExternalServices.Validation;
using Haworks.Content.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Content.Domain.Interfaces;

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

        return services;
    }
}
