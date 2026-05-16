using FluentAssertions;
using Xunit;

namespace Haworks.Tests.Smoke;

/// <summary>
/// Day-0 smoke for {{feature}}-svc. Hits /health on the deployed
/// haworks-{{feature}} flycast endpoint via the shared
/// <see cref="EnvironmentAgnosticFixture"/> HttpClient. Real per-feature
/// smoke checks (golden cart, /price/quote, etc.) are added by L1 tracks
/// or as follow-up work — this is the minimum to prove the deploy lights up.
/// </summary>
[Collection("Smoke Tests")]
public class {{FEATURE}}SmokeTests(EnvironmentAgnosticFixture fixture)
{
    [Fact]
    public async Task {{FEATURE}}_health_endpoint_is_reachable()
    {
        var resp = await fixture.HttpClient.GetAsync("http://haworks-{{feature}}.flycast:8080/health");

        resp.StatusCode.Should().NotBe(System.Net.HttpStatusCode.NotFound,
            "haworks-{{feature}} must be deployed and serving /health");
        resp.IsSuccessStatusCode.Should().BeTrue(
            "/health must return 2xx if {{feature}}-svc is healthy");

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Be("Healthy");
    }
}
