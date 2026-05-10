using System.ComponentModel.DataAnnotations;

namespace Haworks.Notifications.Application.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Notifications:Webhooks";

    public SesWebhookOptions Ses { get; set; } = new();
    public SendGridWebhookOptions SendGrid { get; set; } = new();
    public TwilioWebhookOptions Twilio { get; set; } = new();
}

public sealed class SesWebhookOptions
{
    // SES/SNS doesn't use a shared secret for the payload usually; 
    // it uses signature verification based on the signing certificate URL in the message.
    // However, we might want to restrict by TopicArn.
    public string? TopicArn { get; set; }
}

public sealed class SendGridWebhookOptions
{
    [Required]
    public string WebhookSecret { get; set; } = string.Empty;
}

public sealed class TwilioWebhookOptions
{
    [Required]
    public string AuthToken { get; set; } = string.Empty;
}
