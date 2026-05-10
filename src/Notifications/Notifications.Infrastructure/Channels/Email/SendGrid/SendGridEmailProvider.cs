using Haworks.Notifications.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Haworks.Notifications.Infrastructure.Channels.Email.SendGrid;

/// <summary>
/// SendGrid implementation of <see cref="IEmailProvider"/>.
/// </summary>
public sealed class SendGridEmailProvider : IEmailProvider
{
    public const string ProviderName = "sendgrid";

    private readonly ISendGridClient _client;
    private readonly IOptions<SendGridOptions> _options;
    private readonly ILogger<SendGridEmailProvider> _logger;

    public SendGridEmailProvider(
        ISendGridClient client,
        IOptions<SendGridOptions> options,
        ILogger<SendGridEmailProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => ProviderName;

    public async Task<ProviderSendResult> SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return ProviderSendResult.NonRetryable("Recipient address is required.");
        }

        var message = new SendGridMessage
        {
            From = new EmailAddress(_options.Value.FromAddress),
            Subject = subject
        };
        message.AddTo(new EmailAddress(recipient));
        message.AddContent(MimeType.Text, body);
        message.AddContent(MimeType.Html, body);

        try
        {
            var response = await _client.SendEmailAsync(message, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status >= 200 && status < 300)
            {
                // Extract X-Message-Id from response header
                var messageId = response.Headers.TryGetValues("X-Message-Id", out var values)
                    ? values.FirstOrDefault() ?? string.Empty
                    : string.Empty;

                return ProviderSendResult.Success(messageId);
            }

            if (status == 429)
            {
                _logger.LogWarning("SendGrid throttled the request for recipient {Recipient}", recipient);
                return ProviderSendResult.Retryable("SendGrid throttled: 429 Too Many Requests");
            }

            if (status >= 500)
            {
                _logger.LogWarning("SendGrid returned server error {StatusCode} for recipient {Recipient}", status, recipient);
                return ProviderSendResult.Retryable($"SendGrid server error: HTTP {status}");
            }

            _logger.LogWarning("SendGrid returned client error {StatusCode} for recipient {Recipient}", status, recipient);
            return ProviderSendResult.NonRetryable($"SendGrid client error: HTTP {status}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid unexpected error for recipient {Recipient}", recipient);
            return ProviderSendResult.Retryable($"SendGrid unexpected error: {ex.Message}");
        }
    }
}
