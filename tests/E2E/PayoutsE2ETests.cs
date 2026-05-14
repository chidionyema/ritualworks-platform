using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class PayoutsE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public PayoutsE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _apiContext = await _fixture.CreateApiContextAsync();
    }

    public async Task DisposeAsync()
    {
        await _apiContext.DisposeAsync();
    }

    [Fact]
    public async Task Seller_Registration_And_Onboarding_Flow()
    {
        _output.WriteLine("--- STARTING PAYOUTS E2E TEST ---");
        var sellerId = Guid.NewGuid();
        var email = $"seller_{sellerId:N}@example.com";
        var registerResponse = await _apiContext.PostAsync("/api/Sellers", new APIRequestContextOptions { DataObject = new { sellerId, email } });
        registerResponse.Status.Should().Be(200);
        var registerData = await registerResponse.JsonAsync();
        registerData?.GetProperty("profileId").GetGuid().Should().NotBeEmpty();
        var onboardingResponse = await _apiContext.PostAsync($"/api/Sellers/{sellerId}/onboarding-link?returnUrl=https://example.com&refreshUrl=https://example.com", new APIRequestContextOptions());
        onboardingResponse.Status.Should().Be(200);
        var onboardingData = await onboardingResponse.JsonAsync();
        onboardingData?.GetProperty("url").GetString().Should().StartWith("https://connect.stripe.com/");
    }
}
