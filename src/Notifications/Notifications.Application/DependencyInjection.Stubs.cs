using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Application;

public static partial class DependencyInjection
{
    // L0 stubs — return-as-is. L1+ tracks REPLACE these in their own
    // <Track>ServiceCollectionExtensions.cs files in their owned subdirs.
    //
    // Replaced by tracks: AddNotificationTemplates (L1.B),
    // AddNotificationPreferences (L1.C), AddNotificationSuppressionService (L1.D),
    // AddNotificationIdempotency (L1.E), AddNotificationConsumers (L3).
}
