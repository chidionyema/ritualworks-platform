using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Haworks.Scheduler.Integration;

public class SchedulerIntegrationTests : IClassFixture<SchedulerWebAppFactory>
{
    private readonly SchedulerWebAppFactory _factory;
    private readonly HttpClient _client;

    public SchedulerIntegrationTests(SchedulerWebAppFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Schedule_Should_Return_Accepted_And_Enqueue_Job()
    {
        // Arrange
        var command = new ScheduleEventCommand(
            "integration-test-key-1",
            DateTimeOffset.UtcNow.AddDays(1),
            "test-exchange",
            "test.key",
            "{}");

        // Act
        var response = await _client.PostAsJsonAsync("/api/Scheduling/schedule", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var mockJobClient = _factory.Services.GetRequiredService<IBackgroundJobClient>();
        Mock.Get(mockJobClient).Verify(x => x.Create(
            It.Is<Job>(j => j.Method.Name == "PublishAsync"),
            It.IsAny<ScheduledState>()), Times.Once);
    }
}
