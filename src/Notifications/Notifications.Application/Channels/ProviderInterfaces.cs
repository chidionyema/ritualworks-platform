namespace Haworks.Notifications.Application.Channels;

public interface IEmailProvider
{
    string Name { get; }
    Task<ProviderSendResult> SendAsync(string recipient, string subject, string body, CancellationToken ct);
}

public interface ISmsProvider
{
    string Name { get; }
    Task<ProviderSendResult> SendAsync(string recipient, string body, CancellationToken ct);
}

public interface IPushProvider
{
    string Name { get; }
    Task<ProviderSendResult> SendAsync(string recipient, string subject, string body, CancellationToken ct);
}

public sealed record ProviderSendResult
{
    public string? ProviderMessageId { get; init; }
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public bool IsRetryable { get; init; }

    public static ProviderSendResult Success(string id) => new() { IsSuccess = true, ProviderMessageId = id };
    public static ProviderSendResult Retryable(string error) => new() { IsSuccess = false, Error = error, IsRetryable = true };
    public static ProviderSendResult NonRetryable(string error) => new() { IsSuccess = false, Error = error, IsRetryable = false };
}
