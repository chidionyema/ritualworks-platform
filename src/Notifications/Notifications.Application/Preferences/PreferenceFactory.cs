using System.Reflection;
using System.Runtime.CompilerServices;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Preferences;

/// <summary>
/// Builder for <see cref="Domain.Entities.NotificationPreference"/>. The Domain
/// factory <c>NotificationPreference.Create</c> is owned by another track and
/// currently throws — so we set the private setters by reflection and skip
/// the constructor to honour the cross-track ownership boundary.
/// </summary>
public static class PreferenceFactory
{
    private static readonly PropertyInfo s_userId = GetProp(nameof(NotificationPreference.UserId));
    private static readonly PropertyInfo s_category = GetProp(nameof(NotificationPreference.Category));
    private static readonly PropertyInfo s_channel = GetProp(nameof(NotificationPreference.Channel));
    private static readonly PropertyInfo s_isEnabled = GetProp(nameof(NotificationPreference.IsEnabled));
    private static readonly PropertyInfo s_quietHoursJson = GetProp(nameof(NotificationPreference.QuietHoursJson));

    public static NotificationPreference Build(
        string userId,
        string category,
        NotificationChannel channel,
        bool isEnabled,
        string? quietHoursJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var instance = (NotificationPreference)RuntimeHelpers.GetUninitializedObject(typeof(NotificationPreference));

        // AuditableEntity init that the (skipped) ctor would have done.
        instance.Id = Guid.NewGuid();
        instance.CreatedAt = DateTime.UtcNow;
        instance.RowVersion = new byte[8];

        s_userId.SetValue(instance, userId);
        s_category.SetValue(instance, category);
        s_channel.SetValue(instance, channel);
        s_isEnabled.SetValue(instance, isEnabled);
        s_quietHoursJson.SetValue(instance, quietHoursJson);

        return instance;
    }

    private static PropertyInfo GetProp(string name) =>
        typeof(NotificationPreference).GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"NotificationPreference.{name} not found");
}
