namespace Haworks.Localization.Api.Application;

public interface ICdnService
{
    Task PublishAsync(string key, string locale, string value, CancellationToken ct = default);
}
