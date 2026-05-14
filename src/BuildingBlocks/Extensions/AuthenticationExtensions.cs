using Haworks.BuildingBlocks.Authentication;
using Haworks.BuildingBlocks.CurrentUser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Extensions;

public static class AuthenticationExtensions
{
    /// <summary>
    /// Standard cluster-wide authentication wiring. Registers JWKS-based
    /// JWT validation, HttpContextAccessor, and the ICurrentUserService.
    /// </summary>
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddJwksAuthentication(configuration);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        return services;
    }
}
