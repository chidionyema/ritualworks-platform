using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;
using Haworks.Analytics.Api.Application.Commands;

namespace Haworks.Analytics.Integration;

[Collection("Analytics Integration")]
public sealed class AnalyticsFlowsTests(AnalyticsWebAppFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Health_returns_200()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Track_event_returns_202_Accepted()
    {
        var command = new TrackEventCommand(
            EventId: Guid.NewGuid(),
            EventName: "test_event",
            UserId: Guid.NewGuid(),
            SessionId: "session-123",
            OccurredAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, object> { ["source"] = "test" }
        );

        var response = await _client.PostAsJsonAsync("/api/events", command);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
