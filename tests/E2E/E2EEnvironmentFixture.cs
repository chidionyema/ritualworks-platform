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

    /// <summary>
    /// True when running in CI or when the user explicitly sets E2E_ENABLED=1.
    /// E2E tests are skipped locally by default because the full Aspire stack
    /// (16 services + 10 containers) needs more RAM than most dev machines have.
    /// </summary>
    public static bool IsEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_TARGET_URL")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        Environment.GetEnvironmentVariable("E2E_ENABLED") == "1";

    public string BaseUrl => _baseUrl ?? "http://not-initialized";
    public IPlaywright Playwright => _playwright ?? throw new InvalidOperationException("Playwright not initialized — E2E tests require E2E_ENABLED=1 or CI environment");

    public async Task InitializeAsync()
    {
        if (!IsEnabled) return;

        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__BaseUrl", "http://localhost:9091");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");

        var targetUrl = Environment.GetEnvironmentVariable("E2E_TARGET_URL");

        if (!string.IsNullOrEmpty(targetUrl))
        {
            _baseUrl = targetUrl.TrimEnd('/');
        }
        else
        {
            _appBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.HaworksPlatform_AppHost>();
            _app = await _appBuilder.BuildAsync();

            // Give services more time to start — the full stack (16 services
            // + Vault init/seed + DB migrations) needs well over the default
            // timeout on CI runners.
            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _app.StartAsync(startCts.Token);

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

    /// <summary>
    /// Call at the start of every E2E test to skip when not enabled.
    /// </summary>
    public static void SkipIfNotEnabled()
    {
        Skip.IfNot(IsEnabled,
            "E2E tests are skipped locally. Set E2E_ENABLED=1 or run in CI.");
    }

    public async Task<IAPIRequestContext?> CreateApiContextAsync()
    {
        if (!IsEnabled) return null;
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
