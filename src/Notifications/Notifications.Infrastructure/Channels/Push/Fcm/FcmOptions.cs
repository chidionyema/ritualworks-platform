using System.ComponentModel.DataAnnotations;

namespace Haworks.Notifications.Infrastructure.Channels.Push.Fcm;

/// <summary>
/// Configuration options for the FCM push provider.
/// Bound from the "Notifications:Providers:Fcm" configuration section.
/// </summary>
public sealed class FcmOptions
{
    public const string SectionName = "Notifications:Providers:Fcm";

    /// <summary>When false, FCM provider is not registered and no Firebase credential is required.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string ProjectId { get; set; } = string.Empty;

    [Required]
    public string ServiceAccountJson { get; set; } = string.Empty;
}
