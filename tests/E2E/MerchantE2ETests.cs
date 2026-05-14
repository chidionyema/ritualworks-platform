using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class MerchantE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public MerchantE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
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
    public async Task Create_Merchant_Profile_Should_Return_Success()
    {
        _output.WriteLine("--- STARTING MERCHANT E2E TEST ---");

        var ownerId = Guid.NewGuid();
        var command = new
        {
            ownerId = ownerId,
            name = "Super Store",
            slug = $"super-store-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
        };

        var response = await _apiContext.PostAsync("/api/Merchants", new APIRequestContextOptions
        {
            DataObject = command
        });

        response.Status.Should().Be(200);
        
        var data = await response.JsonAsync();
        data?.GetProperty("merchantId").GetGuid().Should().NotBeEmpty();
    }
}
