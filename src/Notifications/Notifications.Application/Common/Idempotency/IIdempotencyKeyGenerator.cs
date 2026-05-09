namespace Haworks.Notifications.Application.Common.Idempotency;

public interface IIdempotencyKeyGenerator
{
    string Generate(string? userId, string templateId, string recipient, string? callerKey);
}
