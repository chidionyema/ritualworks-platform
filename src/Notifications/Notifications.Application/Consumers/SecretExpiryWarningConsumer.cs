using Haworks.Contracts.Secrets;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Consumers;

/// <summary>
/// Consumes <see cref="SecretExpiryWarningEvent"/> and logs a critical alert.
/// In v2, this will forward to Slack/PagerDuty via the notification provider
/// infrastructure.
/// </summary>
public sealed class SecretExpiryWarningConsumer : IConsumer<SecretExpiryWarningEvent>
{
    private readonly ILogger<SecretExpiryWarningConsumer> _logger;

    public SecretExpiryWarningConsumer(ILogger<SecretExpiryWarningConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<SecretExpiryWarningEvent> context)
    {
        var msg = context.Message;

        _logger.LogCritical(
            "SECRET ROTATION ALERT: {SecretPath} is at {AgePercent:P0} of its TTL. " +
            "Last rotated: {LastRotatedAt:O}. Immediate rotation required.",
            msg.SecretPath, msg.AgePercent, msg.LastRotatedAt);

        return Task.CompletedTask;
    }
}
