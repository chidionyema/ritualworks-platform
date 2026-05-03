using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Haworks.Content.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Content.Domain.Interfaces;

namespace Haworks.Content.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ContentDbContext>((sp, options) => {
            var interceptor = sp.GetRequiredService<DynamicCredentialsConnectionInterceptor>();
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection"))
                   .AddInterceptors(interceptor);
        });

        services.AddScoped<IContentRepository, ContentContextRepository>();

        return services;
    }
}
