using System.ComponentModel.DataAnnotations;

namespace Haworks.Notifications.Infrastructure.Channels.Email.SendGrid;

/// <summary>
/// Configuration options for the SendGrid email provider.
/// Bound from the "Notifications:Providers:SendGrid" configuration section.
/// </summary>
public sealed class SendGridOptions
{
    public const string SectionName = "Notifications:Providers:SendGrid";

    [Required]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = string.Empty;
}
