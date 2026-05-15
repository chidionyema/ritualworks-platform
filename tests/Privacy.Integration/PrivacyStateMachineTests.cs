using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Privacy;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;

namespace Haworks.Privacy.Integration;

/// <summary>
/// End-to-end PrivacyRequestStateMachine tests. Each test publishes events
/// into the in-memory test harness and asserts the saga transitions correctly.
/// Saga state is persisted to real Testcontainers Postgres so the EF saga
/// repository's xmin concurrency and MT outbox semantics are exercised.
/// </summary>
[Collection("Privacy Integration")]
public sealed class PrivacyStateMachineTests : IAsyncLifetime
{
    private readonly PrivacyWebAppFactory _factory;

    public PrivacyStateMachineTests(PrivacyWebAppFactory factory)
    {
        _factory = factory;
    }

    private static bool _warmedUp;

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        if (!_warmedUp)
        {
            // First test: wait for saga endpoint to fully initialize
            await Task.Delay(3000);
            _warmedUp = true;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ----------------------------------------------------------------
    // 1. Happy path — all three services complete => Finalized (row removed)
    // ----------------------------------------------------------------
    [Fact]
    public async Task HappyPath_AllThreeServices_Saga_Finalizes()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        // Publish all three service completions (stagger to avoid concurrency conflicts).
        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "payments-svc");

        // SetCompletedWhenFinalized() removes the row once finalized.
        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(requestId);
            return state == "Completed" || state == "Final" || state is null;
        }, TimeSpan.FromSeconds(15));

        // The saga must have published PrivacyErasureRequested on initiation.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.Published.Select<PrivacyErasureRequested>()
            .Any(p => p.Context.Message.RequestId == requestId).Should().BeTrue();

        // Row may be deleted (finalized). If still present, must be Completed/Final.
        var leftover = await ReadSagaAsync(requestId);
        if (leftover is not null)
        {
            leftover.CurrentState.Should().BeOneOf("Completed", "Final");
            leftover.CompletedAt.Should().NotBeNull();
        }
    }

    // ----------------------------------------------------------------
    // 2. Partial completion (2 of 3) => stays Processing
    // ----------------------------------------------------------------
    [Fact]
    public async Task PartialCompletion_TwoOfThree_StaysProcessing()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        await Task.Delay(500); // Allow concurrency token to settle
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        await Task.Delay(500);

        // Poll until both flags are set (concurrency retries may delay persistence)
        await PollUntilAsync(() =>
        {
            var s = ReadSagaAsync(requestId).Result;
            return s is { IdentityCompleted: true, OrdersCompleted: true };
        }, TimeSpan.FromSeconds(10));

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Processing");
        saga.IdentityCompleted.Should().BeTrue();
        saga.OrdersCompleted.Should().BeTrue();
        saga.PaymentsCompleted.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // 3. Out-of-order completions still finalize
    // ----------------------------------------------------------------
    [Fact]
    public async Task CompletionsArriveOutOfOrder_StillCompletes()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        // Deliberately send in reverse order (stagger to avoid concurrency conflicts).
        await PublishErasureCompletedAsync(requestId, userId, "payments-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(requestId);
            return state == "Completed" || state == "Final" || state is null;
        }, TimeSpan.FromSeconds(15));

        var leftover = await ReadSagaAsync(requestId);
        if (leftover is not null)
        {
            leftover.CurrentState.Should().BeOneOf("Completed", "Final");
        }
    }

    // ----------------------------------------------------------------
    // 4. ErasureFailed => Failed
    // ----------------------------------------------------------------
    [Fact]
    public async Task ErasureFailed_TransitionsToFailed()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishAsync(new PrivacyErasureFailed { RequestId = requestId, UserId = userId, ServiceName = "orders-svc", ErrorMessage = "DB connection lost" });

        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Failed", TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Failed");
    }

    // ----------------------------------------------------------------
    // 5. Timeout => Stalled
    // ----------------------------------------------------------------
    [Fact]
    public async Task Timeout_TransitionsToStalled()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        // Simulate the timeout event directly (in production this fires after 7 days).
        await PublishAsync(new PrivacyErasureTimedOut { RequestId = requestId });

        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Stalled", TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Stalled");
    }

    // ----------------------------------------------------------------
    // 6. Timeout with partial completion => Stalled, with incomplete flags
    // ----------------------------------------------------------------
    [Fact]
    public async Task Timeout_WithPartialCompletion_LogsIncomplete()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        // Only identity completes before timeout.
        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");

        // Wait for identity completion to be persisted before firing the timeout.
        await PollUntilAsync(() =>
        {
            var s = ReadSagaAsync(requestId).GetAwaiter().GetResult();
            return s is { IdentityCompleted: true };
        }, TimeSpan.FromSeconds(10));

        await PublishAsync(new PrivacyErasureTimedOut { RequestId = requestId });
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Stalled", TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Stalled");
        saga.IdentityCompleted.Should().BeTrue();
        saga.OrdersCompleted.Should().BeFalse();
        saga.PaymentsCompleted.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // 7. Duplicate ErasureCompleted after finalized => discarded
    // ----------------------------------------------------------------
    [Fact]
    public async Task DuplicateErasureCompleted_AfterFinalized_IsDiscarded()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "payments-svc");

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(requestId);
            return state == "Completed" || state == "Final" || state is null;
        }, TimeSpan.FromSeconds(15));

        // Send a duplicate — should not throw or create a new saga instance.
        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        // settling time for negative assertion — duplicate must not change finalized state
        try
        {
            await PollUntilAsync(() =>
            {
                var s = ReadSagaAsync(requestId).GetAwaiter().GetResult();
                return s is not null && s.CurrentState != "Completed" && s.CurrentState != "Final";
            }, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* expected — duplicate was correctly discarded */ }

        // Saga should still be finalized (row gone or Completed/Final).
        var saga = await ReadSagaAsync(requestId);
        if (saga is not null)
        {
            saga.CurrentState.Should().BeOneOf("Completed", "Final");
        }
    }

    // ----------------------------------------------------------------
    // 8. ErasureFailed after Completed => discarded (no handler in Completed)
    // ----------------------------------------------------------------
    [Fact]
    public async Task ErasureFailed_AfterCompleted_IsDiscarded()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "payments-svc");

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(requestId);
            return state == "Completed" || state == "Final" || state is null;
        }, TimeSpan.FromSeconds(15));

        // Publish failure after completion — saga is finalized, event is discarded.
        await PublishAsync(new PrivacyErasureFailed { RequestId = requestId, UserId = userId, ServiceName = "orders-svc", ErrorMessage = "late failure" });
        // settling time for negative assertion — late failure must not move finalized saga
        try
        {
            await PollUntilAsync(() =>
            {
                var s = ReadSagaAsync(requestId).GetAwaiter().GetResult();
                return s is not null && s.CurrentState == "Failed";
            }, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* expected — late failure was correctly discarded */ }

        var saga = await ReadSagaAsync(requestId);
        if (saga is not null)
        {
            saga.CurrentState.Should().BeOneOf("Completed", "Final");
        }
    }

    // ----------------------------------------------------------------
    // 9. ErasureCompleted after Failed => discarded (no handler in Failed)
    // ----------------------------------------------------------------
    [Fact]
    public async Task ErasureCompleted_AfterFailed_IsDiscarded()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishAsync(new PrivacyErasureFailed { RequestId = requestId, UserId = userId, ServiceName = "identity-svc", ErrorMessage = "crash" });
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Failed", TimeSpan.FromSeconds(15));

        // Publish completion after failure — should be discarded.
        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        // settling time for negative assertion — late completion must not move Failed saga
        try
        {
            await PollUntilAsync(() => SagaStateOrNull(requestId) != "Failed", TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* expected — completion after failure was correctly discarded */ }

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Failed");
    }

    // ----------------------------------------------------------------
    // 10. Unknown service => stays Processing (logs warning)
    // ----------------------------------------------------------------
    [Fact]
    public async Task UnknownService_LogsWarning_StaysProcessing()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishErasureCompletedAsync(requestId, userId, "unknown-svc");
        // settling time for negative assertion — unknown service must not set any flags
        try
        {
            await PollUntilAsync(() =>
            {
                var s = ReadSagaAsync(requestId).GetAwaiter().GetResult();
                return s is { IdentityCompleted: true } or { OrdersCompleted: true } or { PaymentsCompleted: true };
            }, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* expected — unknown service was correctly ignored */ }

        var saga = await ReadSagaAsync(requestId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("Processing");
        saga.IdentityCompleted.Should().BeFalse();
        saga.OrdersCompleted.Should().BeFalse();
        saga.PaymentsCompleted.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // 11. State persists across harness restart
    // ----------------------------------------------------------------
    [Fact]
    public async Task StatePersists_AcrossRestart()
    {
        var (requestId, userId) = await PublishInitiateAsync();
        await PollUntilAsync(() => SagaStateOrNull(requestId) == "Processing", TimeSpan.FromSeconds(15));

        await PublishErasureCompletedAsync(requestId, userId, "identity-svc");

        // Wait for identity completion to be persisted before stopping harness.
        await PollUntilAsync(() =>
        {
            var s = ReadSagaAsync(requestId).GetAwaiter().GetResult();
            return s is { IdentityCompleted: true };
        }, TimeSpan.FromSeconds(10));

        // Stop the harness — simulates the service pod going away.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Stop();

        // Saga state must still be in the DB.
        var persisted = await ReadSagaAsync(requestId);
        persisted.Should().NotBeNull("EF saga repository must persist state across restarts");
        persisted!.CurrentState.Should().Be("Processing");
        persisted.IdentityCompleted.Should().BeTrue();

        // Restart and verify the saga can pick up where it left off.
        await harness.Start();
        await Task.Delay(2000); // warmup after restart

        await PublishErasureCompletedAsync(requestId, userId, "orders-svc");
        await Task.Delay(500);
        await PublishErasureCompletedAsync(requestId, userId, "payments-svc");

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(requestId);
            return state == "Completed" || state == "Final" || state is null;
        }, TimeSpan.FromSeconds(15));

        var resumed = await ReadSagaAsync(requestId);
        if (resumed is not null)
        {
            resumed.CurrentState.Should().BeOneOf("Completed", "Final");
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private async Task<(Guid requestId, Guid userId)> PublishInitiateAsync()
    {
        var requestId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await PublishAsync(new InitiatePrivacyRequestMessage { RequestId = requestId, UserId = userId });
        return (requestId, userId);
    }

    private Task PublishErasureCompletedAsync(Guid requestId, Guid userId, string serviceName)
        => PublishAsync(new PrivacyErasureCompleted { RequestId = requestId, UserId = userId, ServiceName = serviceName });

    private async Task PublishAsync<T>(T evt) where T : class
    {
        var bus = _factory.Services.GetRequiredService<MassTransit.IBus>();
        await bus.Publish(evt);
    }

    private string? SagaStateOrNull(Guid requestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        return db.Set<PrivacyRequestState>().AsNoTracking()
            .Where(s => s.CorrelationId == requestId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<PrivacyRequestState?> ReadSagaAsync(Guid requestId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        return await db.Set<PrivacyRequestState>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == requestId);
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"PollUntilAsync timed out after {timeout.TotalSeconds}s in {caller}");
    }
}
