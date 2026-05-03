using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Haworks.Identity.Infrastructure;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppIdentityDbContext>((sp, options) => {
            var interceptor = sp.GetRequiredService<DynamicCredentialsConnectionInterceptor>();
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection"))
                   .AddInterceptors(interceptor);
        });

        services.AddIdentity<User, IdentityRole>()
                .AddEntityFrameworkStores<AppIdentityDbContext>()
                .AddDefaultTokenProviders();

        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        return services;
    }
}
