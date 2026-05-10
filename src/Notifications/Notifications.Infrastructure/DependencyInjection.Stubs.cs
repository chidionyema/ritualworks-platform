using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Notifications.Infrastructure;

public static partial class DependencyInjection
{
    // L0 stubs — return-as-is. Tracks REPLACE these in their own
    // <Track>ServiceCollectionExtensions.cs files in their owned subdirs.
    //
    // Replaced by tracks: AddNotificationTemplatesPersistence (L1.B),
    // AddSesEmailProvider (L2.H), AddSendGridEmailProvider (F1),
    // AddTwilioSmsProvider (F2), AddFcmPushProvider (F3),
    // AddNotificationChannelGateways (L3).
}
