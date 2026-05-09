using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Infrastructure;

public static partial class DependencyInjection
{
    // L0 stubs — return-as-is. Tracks REPLACE these in their own
    // <Track>ServiceCollectionExtensions.cs files in their owned subdirs.
    //
    // Replaced by tracks: AddNotificationTemplatesPersistence (L1.B),
    // AddSesEmailProvider (L2.H), AddNotificationChannelGateways (L3).
    internal static IServiceCollection AddTwilioSmsProvider(this IServiceCollection s, IConfiguration c) => s;
    internal static IServiceCollection AddFcmPushProvider(this IServiceCollection s, IConfiguration c) => s;
}
