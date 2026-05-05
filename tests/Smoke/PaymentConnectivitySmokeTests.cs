using FluentAssertions;
using Xunit;

namespace Haworks.Tests.Smoke;

[Collection("Smoke Tests")]
public class PaymentConnectivitySmokeTests(EnvironmentAgnosticFixture fixture)
{
    [Fact]
    public async Task Payment_Provider_Connectivity_IsVerified()
    {
        var response = await fixture.HttpClient.GetAsync("/health");
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Healthy");
    }
}
