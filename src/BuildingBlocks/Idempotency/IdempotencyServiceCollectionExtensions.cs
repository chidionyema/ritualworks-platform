using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Idempotency;

public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Register the Postgres-backed <see cref="IIdempotencyStore"/> against
    /// the given bounded-context DbContext. Each service owns its own
    /// idempotency_claims table inside its own DB, preserving bounded-context
    /// isolation -- no cross-context table sharing.
    /// </summary>
    public static IServiceCollection AddPostgresIdempotency<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore<TDbContext>>();
        return services;
    }

    /// <summary>
    /// Mount the idempotency middleware. Place it AFTER UseAuthentication
    /// (so ICurrentUserService.UserId is populated) and BEFORE any
    /// mutating endpoint registration. Requests without
    /// <c>X-Idempotency-Key</c> pass through untouched.
    /// </summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyMiddleware>();
}
