using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class PrivacyE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public PrivacyE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
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
    public async Task Initiate_Erasure_Request_Should_Return_Accepted()
    {
        _output.WriteLine("--- STARTING PRIVACY E2E TEST ---");

        var userId = Guid.NewGuid();
        var command = new
        {
            userId = userId,
            type = "Erasure"
        };

        var response = await _apiContext.PostAsync("/api/PrivacyRequests", new APIRequestContextOptions
        {
            DataObject = command
        });

        response.Status.Should().Be(200); // We currently return Ok with RequestId
        
        var data = await response.JsonAsync();
        data?.GetProperty("requestId").GetGuid().Should().NotBeEmpty();
    }
}
