using Microsoft.Extensions.DependencyInjection;
using Haworks.Notifications.Application.Channels;

namespace Haworks.Notifications.Infrastructure;

/// <summary>
/// L3 DI registration. Replaces the L0 stub
/// <c>DependencyInjection.AddNotificationChannelGateways</c>.
///
/// Registers the email gateway as Scoped because Scoped <c>IEmailProvider</c>
/// implementations (e.g., the SES provider from L2.H) cannot be injected into
/// a Singleton without an extra scope-factory hop. The gateway's per-provider
/// circuit-breaker state lives in a <c>static</c> dictionary on the gateway
/// type so it persists across Scoped instances. SMS / push gateways follow
/// the same pattern when other tracks land.
/// </summary>
public static class EmailChannelServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationChannelGateways(this IServiceCollection services)
    {
        services.AddScoped<IEmailChannelGateway,
            Notifications.Infrastructure.Channels.Email.EmailChannelGateway>();
        return services;
    }
}
