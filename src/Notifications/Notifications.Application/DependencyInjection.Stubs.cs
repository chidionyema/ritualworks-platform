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
    //
    // L1.B replaced AddNotificationTemplates — see Templates/TemplatesServiceCollectionExtensions.cs.
    internal static IServiceCollection AddNotificationConsumers(this IServiceCollection s) => s;
}
