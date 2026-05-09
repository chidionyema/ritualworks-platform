using System.ComponentModel.DataAnnotations;

namespace Haworks.Notifications.Infrastructure.Channels.Email.Ses;

/// <summary>
/// Configuration options for the AWS SES email provider.
/// Bound from the "Notifications:Providers:Ses" configuration section.
/// </summary>
public sealed class SesOptions
{
    public const string SectionName = "Notifications:Providers:Ses";

    [Required]
    public string AccessKey { get; set; } = string.Empty;

    [Required]
    public string SecretKey { get; set; } = string.Empty;

    [Required]
    public string Region { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = string.Empty;
}
