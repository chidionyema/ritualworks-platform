using Haworks.Notifications.Application.Common.Idempotency;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

/// <summary>
/// L1.E DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationIdempotency</c>.
/// </summary>
public static class IdempotencyServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationIdempotency(this IServiceCollection services)
    {
        services.AddSingleton<IIdempotencyKeyGenerator, IdempotencyKeyGenerator>();
        return services;
    }
}
