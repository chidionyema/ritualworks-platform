using Haworks.Notifications.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Haworks.Notifications.Infrastructure.Channels.Sms.Twilio;

/// <summary>
/// Twilio implementation of <see cref="ISmsProvider"/>.
/// </summary>
public sealed class TwilioSmsProvider : ISmsProvider
{
    public const string ProviderName = "twilio";

    private readonly ITwilioRestClient _client;
    private readonly IOptions<TwilioOptions> _options;
    private readonly ILogger<TwilioSmsProvider> _logger;

    public TwilioSmsProvider(
        ITwilioRestClient client,
        IOptions<TwilioOptions> options,
        ILogger<TwilioSmsProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => ProviderName;

    public async Task<ProviderSendResult> SendAsync(
        string recipient,
        string body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return ProviderSendResult.NonRetryable("Recipient number is required.");
        }

        try
        {
            var message = await MessageResource.CreateAsync(
                to: new PhoneNumber(recipient),
                from: new PhoneNumber(_options.Value.FromNumber),
                body: body,
                client: _client
            ).ConfigureAwait(false);

            if (message.ErrorCode.HasValue)
            {
                var error = $"Twilio error {message.ErrorCode}: {message.ErrorMessage}";
                _logger.LogWarning("Twilio returned error for recipient {Recipient}: {Error}", recipient, error);
                
                return MapErrorCode(message.ErrorCode.Value, error);
            }

            return ProviderSendResult.Success(message.Sid);
        }
        catch (ApiException ex)
        {
            var error = $"Twilio API exception: {ex.Message} (Code: {ex.Code})";
            _logger.LogWarning(ex, "Twilio API exception for recipient {Recipient}", recipient);
            
            return MapErrorCode(ex.Code, error);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return ProviderSendResult.Retryable("Twilio request timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending SMS via Twilio to {Recipient}", recipient);
            return ProviderSendResult.Retryable($"Unexpected Twilio error: {ex.Message}");
        }
    }

    private static ProviderSendResult MapErrorCode(int code, string message)
    {
        // Twilio error codes: https://www.twilio.com/docs/api/errors
        // 20xxx: API Errors (usually NonRetryable validation/auth)
        // 30xxx: Delivery Errors (carrier rejection, etc. - usually NonRetryable for this specific msg)
        // 50xxx: Server errors (Retryable)
        
        if (code >= 50000 && code < 60000)
        {
            return ProviderSendResult.Retryable(message);
        }

        if (code >= 20000 && code < 40000)
        {
            return ProviderSendResult.NonRetryable(message);
        }

        // Default to Retryable for unknown codes unless they look like 4xx equivalents
        return code >= 400 && code < 500 
            ? ProviderSendResult.NonRetryable(message) 
            : ProviderSendResult.Retryable(message);
    }
}
