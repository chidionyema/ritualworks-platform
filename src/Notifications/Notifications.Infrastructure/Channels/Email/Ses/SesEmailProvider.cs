using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Haworks.Notifications.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Notifications.Infrastructure.Channels.Email.Ses;

/// <summary>
/// AWS SES (v2) implementation of <see cref="IEmailProvider"/>.
/// </summary>
public sealed class SesEmailProvider : IEmailProvider
{
    public const string ProviderName = "aws-ses";

    private readonly IAmazonSimpleEmailServiceV2 _client;
    private readonly IOptions<SesOptions> _options;
    private readonly ILogger<SesEmailProvider> _logger;

    public SesEmailProvider(
        IAmazonSimpleEmailServiceV2 client,
        IOptions<SesOptions> options,
        ILogger<SesEmailProvider> logger)
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

        var request = new SendEmailRequest
        {
            FromEmailAddress = _options.Value.FromAddress,
            Destination = new Destination
            {
                ToAddresses = new List<string> { recipient }
            },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject ?? string.Empty },
                    Body = new Body
                    {
                        Html = new Content { Data = body ?? string.Empty },
                        Text = new Content { Data = body ?? string.Empty }
                    }
                }
            }
        };

        try
        {
            var response = await _client.SendEmailAsync(request, ct).ConfigureAwait(false);

            var status = (int)response.HttpStatusCode;
            if (status >= 200 && status < 300)
            {
                return ProviderSendResult.Success(response.MessageId ?? string.Empty);
            }

            if (status >= 500)
            {
                _logger.LogWarning(
                    "SES returned server error {StatusCode} for recipient {Recipient}",
                    status, recipient);
                return ProviderSendResult.Retryable(
                    $"SES server error: HTTP {status}");
            }

            _logger.LogWarning(
                "SES returned client error {StatusCode} for recipient {Recipient}",
                status, recipient);
            return ProviderSendResult.NonRetryable(
                $"SES client error: HTTP {status}");
        }
        catch (TooManyRequestsException ex)
        {
            _logger.LogWarning(ex, "SES throttled the request for recipient {Recipient}", recipient);
            return ProviderSendResult.Retryable($"SES throttled: {ex.Message}");
        }
        catch (SendingPausedException ex)
        {
            _logger.LogError(ex, "SES sending is paused for the configuration set");
            return ProviderSendResult.Retryable($"SES sending paused: {ex.Message}");
        }
        catch (MailFromDomainNotVerifiedException ex)
        {
            _logger.LogError(ex, "SES MAIL FROM domain not verified");
            return ProviderSendResult.NonRetryable($"SES MAIL FROM domain not verified: {ex.Message}");
        }
        catch (MessageRejectedException ex)
        {
            _logger.LogWarning(ex, "SES rejected the message for recipient {Recipient}", recipient);
            return ProviderSendResult.NonRetryable($"SES rejected message: {ex.Message}");
        }
        catch (AccountSuspendedException ex)
        {
            _logger.LogError(ex, "SES account is suspended");
            return ProviderSendResult.NonRetryable($"SES account suspended: {ex.Message}");
        }
        catch (AmazonSimpleEmailServiceV2Exception ex)
        {
            var status = (int)ex.StatusCode;
            if (status >= 500 || status == 0)
            {
                _logger.LogWarning(ex,
                    "SES transient failure (status {StatusCode}) for recipient {Recipient}",
                    status, recipient);
                return ProviderSendResult.Retryable($"SES transient failure: {ex.Message}");
            }

            _logger.LogWarning(ex,
                "SES validation/client failure (status {StatusCode}) for recipient {Recipient}",
                status, recipient);
            return ProviderSendResult.NonRetryable($"SES validation failure: {ex.Message}");
        }
    }
}
