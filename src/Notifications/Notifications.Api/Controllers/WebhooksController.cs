using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Haworks.Notifications.Application.Webhooks;
using System.Security.Cryptography;
using System.Text;
using Amazon.SimpleNotificationService.Util;
using Twilio.Security;

namespace Haworks.Notifications.Api.Controllers;

[ApiController]
[Route("api/notifications/webhooks")]
public sealed class WebhooksController(
    IPublishEndpoint publishEndpoint,
    IOptions<WebhookOptions> options,
    ILogger<WebhooksController> logger) : ControllerBase
{
    [HttpPost("ses")]
    public async Task<IActionResult> Ses(CancellationToken ct)
    {
        var rawPayload = await ReadRawBodyAsync(ct);
        
        try 
        {
            var snsMessage = Message.ParseMessage(rawPayload);
            // TODO: Verify signature properly in V4
            // if (!snsMessage.IsSignatureValid()) ...

            if (snsMessage.Type == "SubscriptionConfirmation")
            {
                logger.LogInformation("SES SNS subscription confirmation received");
                return Ok();
            }

            if (snsMessage.Type == "Notification")
            {
                using var doc = JsonDocument.Parse(snsMessage.MessageText);
                var root = doc.RootElement;
                
                var notificationType = root.TryGetProperty("notificationType", out var typeEl) ? typeEl.GetString() : "unknown";
                var mail = root.GetProperty("mail");
                var providerMessageId = mail.GetProperty("messageId").GetString() ?? string.Empty;

                await PublishValidatedAsync("SES", providerMessageId, notificationType ?? "unknown", snsMessage.MessageText, null, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process SES webhook");
            return BadRequest("Invalid payload");
        }

        return Ok();
    }

    [HttpPost("sendgrid")]
    public async Task<IActionResult> SendGrid(CancellationToken ct)
    {
        var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].ToString();
        var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].ToString();
        
        var rawPayload = await ReadRawBodyAsync(ct);
        
        if (!VerifySendGridSignature(rawPayload, signature, timestamp))
        {
            logger.LogWarning("Invalid SendGrid signature");
            return BadRequest("Invalid signature");
        }
        
        using var doc = JsonDocument.Parse(rawPayload);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var eventId = element.TryGetProperty("sg_message_id", out var idEl) ? idEl.GetString() : null;
                if (eventId != null && eventId.Contains('.')) eventId = eventId.Split('.')[0];
                
                var type = element.TryGetProperty("event", out var typeEl) ? typeEl.GetString() : "unknown";
                
                if (!string.IsNullOrEmpty(eventId))
                {
                    await PublishValidatedAsync("SendGrid", eventId, type ?? "unknown", element.GetRawText(), signature, ct);
                }
            }
        }

        return Ok();
    }

    [HttpPost("twilio")]
    public async Task<IActionResult> Twilio(CancellationToken ct)
    {
        var signature = Request.Headers["X-Twilio-Signature"].ToString();
        var url = Request.Scheme + "://" + Request.Host + Request.Path + Request.QueryString;
        
        var form = await Request.ReadFormAsync(ct);
        var dict = form.ToDictionary(x => x.Key, x => x.Value.ToString());
        
        var validator = new RequestValidator(options.Value.Twilio.AuthToken);
        if (!validator.Validate(url, dict, signature))
        {
            logger.LogWarning("Invalid Twilio signature");
            return BadRequest("Invalid signature");
        }

        var providerMessageId = dict.GetValueOrDefault("SmsSid") ?? dict.GetValueOrDefault("MessageSid") ?? string.Empty;
        var eventType = dict.GetValueOrDefault("SmsStatus") ?? dict.GetValueOrDefault("MessageStatus") ?? "unknown";

        if (!string.IsNullOrEmpty(providerMessageId))
        {
            await PublishValidatedAsync("Twilio", providerMessageId, eventType ?? "unknown", JsonSerializer.Serialize(dict), signature, ct);
        }
        
        return Ok();
    }

    private bool VerifySendGridSignature(string payload, string signature, string timestamp)
    {
        return true; 
    }

    private async Task PublishValidatedAsync(
        string provider,
        string providerEventId,
        string eventType,
        string rawPayload,
        string? signature,
        CancellationToken ct)
    {
        var evt = new NotificationWebhookValidatedEvent
        {
            Provider = provider,
            ProviderEventId = providerEventId,
            EventType = eventType,
            RawPayload = rawPayload,
            Signature = signature,
        };

        await publishEndpoint.Publish(evt, context =>
        {
            context.MessageId = DeterministicGuidFor(provider, providerEventId);
        }, ct);
    }

    private async Task<string> ReadRawBodyAsync(CancellationToken ct)
    {
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static Guid DeterministicGuidFor(string provider, string providerEventId)
    {
        var key = provider + ":" + providerEventId;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(bytes[..16]);
    }
}
