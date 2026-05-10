using FirebaseAdmin.Messaging;
using Haworks.Notifications.Application.Channels;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Infrastructure.Channels.Push.Fcm;

/// <summary>
/// Firebase Cloud Messaging (FCM) implementation of <see cref="IPushProvider"/>.
/// </summary>
public sealed class FcmPushProvider : IPushProvider
{
    public const string ProviderName = "fcm";

    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmPushProvider> _logger;

    public FcmPushProvider(
        FirebaseMessaging messaging,
        ILogger<FcmPushProvider> logger)
    {
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
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
            return ProviderSendResult.NonRetryable("Recipient token is required.");
        }

        var message = new Message
        {
            Token = recipient,
            Notification = new Notification
            {
                Title = subject,
                Body = body
            }
        };

        try
        {
            var messageId = await _messaging.SendAsync(message, ct).ConfigureAwait(false);
            return ProviderSendResult.Success(messageId);
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogWarning(ex, "FCM send failed for recipient {Recipient}. ErrorCode: {ErrorCode}", recipient, ex.MessagingErrorCode);

            return ex.MessagingErrorCode switch
            {
                MessagingErrorCode.InvalidArgument or MessagingErrorCode.Unregistered 
                    => ProviderSendResult.NonRetryable($"FCM error: {ex.MessagingErrorCode} - {ex.Message}"),
                
                MessagingErrorCode.Unavailable or MessagingErrorCode.Internal 
                    => ProviderSendResult.Retryable($"FCM error: {ex.MessagingErrorCode} - {ex.Message}"),

                _ => ProviderSendResult.NonRetryable($"FCM unexpected error: {ex.MessagingErrorCode} - {ex.Message}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending FCM notification to {Recipient}", recipient);
            return ProviderSendResult.NonRetryable($"FCM unexpected error: {ex.Message}");
        }
    }
}
