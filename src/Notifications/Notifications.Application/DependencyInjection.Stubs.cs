using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

public static partial class DependencyInjection
{
    // L0 stubs — return-as-is. L1+ tracks REPLACE these in their own
    // <Track>ServiceCollectionExtensions.cs files in their owned subdirs.
    // (Keep the L0 entries as plain extensions, NOT partial methods —
    // partials with non-void return require both defining + implementing
    // halves; switching to plain extensions lets each track ship in its
    // own commit without needing a coordinator pre-merge.)
    internal static IServiceCollection AddNotificationTemplates(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationPreferences(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationSuppressionService(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationIdempotency(this IServiceCollection s) => s;
    internal static IServiceCollection AddNotificationConsumers(this IServiceCollection s) => s;
}
