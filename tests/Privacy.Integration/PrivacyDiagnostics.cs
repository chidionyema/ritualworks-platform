using FluentAssertions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Privacy.Integration;

public sealed class PrivacyDiagnostics : IClassFixture<PrivacyWebAppFactory>, IAsyncLifetime
{
    private readonly PrivacyWebAppFactory _factory;
    public PrivacyDiagnostics(PrivacyWebAppFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Diag_Publish_And_Check()
    {
        var requestId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        Console.WriteLine($"DB Connection: {db.Database.GetConnectionString()}");
        Console.WriteLine($"Factory ConnStr: {_factory.ConnectionString}");

        // Try manual insert
        var manualState = new PrivacyRequestState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "TestManual",
            UserId = userId,
            RequestType = "Test",
            CreatedAt = DateTime.UtcNow,
        };
        db.Set<PrivacyRequestState>().Add(manualState);
        try
        {
            await db.SaveChangesAsync();
            Console.WriteLine("Manual insert: SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Manual insert: FAILED - {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"  Inner: {ex.InnerException.Message}");
        }

        // Publish the event
        var publisher = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        await publisher.Publish(new InitiatePrivacyRequestMessage(requestId, userId));

        await Task.Delay(5000);

        var sagaHarness = harness.GetSagaStateMachineHarness<PrivacyRequestStateMachine, PrivacyRequestState>();
        Console.WriteLine($"Saga consumed: {await sagaHarness.Consumed.Any<InitiatePrivacyRequestMessage>()}");
        Console.WriteLine($"Any saga created: {await sagaHarness.Created.Any()}");

        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        var allStates = await db2.Set<PrivacyRequestState>().AsNoTracking().ToListAsync();
        Console.WriteLine($"Saga rows in DB: {allStates.Count}");
        foreach (var s in allStates)
            Console.WriteLine($"  {s.CorrelationId}: {s.CurrentState}");

        allStates.Count.Should().BeGreaterThan(0, "saga state should have been persisted to DB");
    }
}
