using Haworks.Localization.Api.Application;

namespace Haworks.Localization.Api.Infrastructure;

public class MockCdnService : ICdnService
{
    private readonly ILogger<MockCdnService> _logger;

    public MockCdnService(ILogger<MockCdnService> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(string key, string locale, string value, CancellationToken ct = default)
    {
        _logger.LogInformation("CDN Publish: {Key} [{Locale}] = {Value}", key, locale, value);
        return Task.CompletedTask;
    }
}
