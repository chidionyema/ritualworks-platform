using MediatR;
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
}
