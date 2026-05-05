using FluentAssertions;
using Xunit;

namespace Haworks.Tests.Smoke;

[Collection("Smoke Tests")]
public class ApiHealthSmokeTests(EnvironmentAgnosticFixture fixture)
{
    [Fact]
    public async Task Root_Endpoint_IsReachable()
    {
        var response = await fixture.HttpClient.GetAsync("/");
        response.StatusCode.Should().NotBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_Endpoint_ReturnsHealthy()
    {
        var response = await fixture.HttpClient.GetAsync("/health");
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }
}
