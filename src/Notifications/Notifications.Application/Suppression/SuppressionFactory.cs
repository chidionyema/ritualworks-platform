using System.Reflection;
using System.Runtime.CompilerServices;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Suppression;

/// <summary>
/// Internal builder for <see cref="Domain.Entities.Suppression"/> aggregates.
///
/// The Domain entity uses a private parameterless constructor and private
/// setters so EF Core can hydrate it. The L0 stub of <c>Suppression.Create</c>
/// throws — and Domain is owned by another track, so we reflect over the
/// declared private setters here in Application instead of mutating Domain.
/// </summary>
internal static class SuppressionFactory
{
    private static readonly PropertyInfo s_recipientHash = GetProp(nameof(Domain.Entities.Suppression.RecipientHash));
    private static readonly PropertyInfo s_channel = GetProp(nameof(Domain.Entities.Suppression.Channel));
    private static readonly PropertyInfo s_reason = GetProp(nameof(Domain.Entities.Suppression.Reason));
    private static readonly PropertyInfo s_sourceEventId = GetProp(nameof(Domain.Entities.Suppression.SourceEventId));

    public static Domain.Entities.Suppression Create(
        string recipientHash,
        NotificationChannel channel,
        string reason,
        string? sourceEventId)
    {
        var instance = (Domain.Entities.Suppression)RuntimeHelpers.GetUninitializedObject(typeof(Domain.Entities.Suppression));

        // Manually initialize the AuditableEntity base fields that the
        // (skipped) constructor would normally set.
        instance.Id = Guid.NewGuid();
        instance.CreatedAt = DateTime.UtcNow;
        instance.RowVersion = new byte[8];

        s_recipientHash.SetValue(instance, recipientHash);
        s_channel.SetValue(instance, channel);
        s_reason.SetValue(instance, reason);
        s_sourceEventId.SetValue(instance, sourceEventId);

        return instance;
    }

    private static PropertyInfo GetProp(string name) =>
        typeof(Domain.Entities.Suppression).GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Suppression.{name} not found");
}
