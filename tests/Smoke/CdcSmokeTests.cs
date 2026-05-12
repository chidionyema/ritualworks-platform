using FluentAssertions;
using Xunit;

namespace Haworks.Tests.Smoke;

/// <summary>
/// Day-0 smoke for cdc-svc. Hits /health on the deployed
/// ritualworks-cdc flycast endpoint via the shared
/// <see cref="EnvironmentAgnosticFixture"/> HttpClient. Real per-feature
/// smoke checks (golden cart, /price/quote, etc.) are added by L1 tracks
/// or as follow-up work — this is the minimum to prove the deploy lights up.
/// </summary>
[Collection("Smoke Tests")]
public class CdcSmokeTests(EnvironmentAgnosticFixture fixture)
{
    [Fact]
    public async Task Cdc_health_endpoint_is_reachable()
    {
        var resp = await fixture.HttpClient.GetAsync("http://ritualworks-cdc.flycast:8080/health");

        resp.StatusCode.Should().NotBe(System.Net.HttpStatusCode.NotFound,
            "ritualworks-cdc must be deployed and serving /health");
        resp.IsSuccessStatusCode.Should().BeTrue(
            "/health must return 2xx if cdc-svc is healthy");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }
}
