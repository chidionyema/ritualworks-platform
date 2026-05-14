using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit;

namespace Haworks.Tests.E2E;

public sealed class E2EEnvironmentFixture : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? _appBuilder;
    private DistributedApplication? _app;
    private IPlaywright? _playwright;
    private string? _baseUrl;

    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("Fixture not initialized");
    public IPlaywright Playwright => _playwright ?? throw new InvalidOperationException("Playwright not initialized");

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__BaseUrl", "http://localhost:9091");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");

        var targetUrl = Environment.GetEnvironmentVariable("E2E_TARGET_URL");

        if (!string.IsNullOrEmpty(targetUrl))
        {
            _baseUrl = targetUrl.TrimEnd('/');
        }
        else
        {
            _appBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RitualworksPlatform_AppHost>();
            _app = await _appBuilder.BuildAsync();
            await _app.StartAsync();

            _baseUrl = _app.GetEndpoint("bff-web").ToString().TrimEnd('/');
        }

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    public async Task<IAPIRequestContext> CreateApiContextAsync()
    {
        return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true
        });
    }

    public Uri GetServiceEndpoint(string name)
    {
        return _app?.GetEndpoint(name) ?? throw new InvalidOperationException("App not started");
    }
}

[CollectionDefinition("E2E Tests")]
public class E2ETestSuiteCollection : ICollectionFixture<E2EEnvironmentFixture> { }
