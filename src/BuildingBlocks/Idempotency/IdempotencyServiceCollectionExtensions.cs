using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Idempotency;

public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the idempotency pipeline behavior for MediatR.
    /// Call this AFTER AddMediatR() in your service's DI setup.
    /// The behavior intercepts all commands implementing <see cref="IIdempotentCommand"/>
    /// and enforces at-most-once execution via the IdempotencyJournal table.
    /// </summary>
    public static IServiceCollection AddIdempotencyBehavior(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        return services;
    }

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
