using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Infrastructure;

public static partial class DependencyInjection
{
    // L0 stubs — return-as-is. Tracks REPLACE these in their own
    // <Track>ServiceCollectionExtensions.cs files in their owned subdirs.
    internal static IServiceCollection AddNotificationTemplatesPersistence(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationPreferencesPersistence(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationSuppressionPersistence(this IServiceCollection s) => s;
    internal static IServiceCollection AddSesEmailProvider(this IServiceCollection s, IConfiguration c) => s;
    internal static IServiceCollection AddSendGridEmailProvider(this IServiceCollection s, IConfiguration c) => s;
    internal static IServiceCollection AddTwilioSmsProvider(this IServiceCollection s, IConfiguration c) => s;
    internal static IServiceCollection AddFcmPushProvider(this IServiceCollection s, IConfiguration c) => s;
    internal static IServiceCollection AddNotificationChannelGateways(this IServiceCollection s) => s;
}
