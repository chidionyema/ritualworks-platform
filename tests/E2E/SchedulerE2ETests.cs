using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Tests.E2E;

[Collection("E2E Tests")]
public class SchedulerE2ETests : IAsyncLifetime
{
    private readonly E2EEnvironmentFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IAPIRequestContext _apiContext = null!;

    public SchedulerE2ETests(E2EEnvironmentFixture fixture, ITestOutputHelper output)
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
    public async Task Schedule_Event_Should_Return_Accepted()
    {
        _output.WriteLine("--- STARTING SCHEDULER E2E TEST ---");

        var payload = new { OrderId = Guid.NewGuid(), Reason = "E2E Test" };
        var command = new
        {
            scheduledTime = DateTimeOffset.UtcNow.AddMinutes(1),
            targetExchange = "e2e-exchange",
            routingKey = "e2e.test",
            payload = payload
        };

        var response = await _apiContext.PostAsync("/api/Scheduling/schedule", new APIRequestContextOptions
        {
            DataObject = command
        });

        response.Status.Should().Be(202); // Accepted
    }
}
