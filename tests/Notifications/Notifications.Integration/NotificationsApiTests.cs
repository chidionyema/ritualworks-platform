using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Suppression;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Testing;

namespace Haworks.Notifications.Integration;

// TODO(notif-L4): Notifications.Api Program.cs cannot boot the WebApplicationFactory
// host because BOTH Notifications.Infrastructure.AddNotificationsInfrastructure AND
// Notifications.Application.AddNotificationsApplication call services.AddMassTransit(...).
// MassTransit v8.3.4 throws ConfigurationException("AddMassTransit() was already called
// and may only be called once per container") on the second call. The L3 author's claim
// that AddMassTransit is "additive across multiple AddMassTransit calls" is incorrect
// for MT v8 — additivity applies to AddConsumer<T>(), which must be called outside of
// a wrapping AddMassTransit (or both registrations must be merged into a single
// AddMassTransit call). Per L4 constraints (no service-side fixes), all five tests
// below carry [Fact(Skip=...)] until a follow-up track collapses
// NotificationConsumersServiceCollectionExtensions.AddNotificationConsumers into the
// single AddMassTransit invocation in Notifications.Infrastructure.DependencyInjection,
// or replaces it with services.AddConsumer<NotificationRequestConsumer>() (no wrapping).

/// <summary>
/// HTTP-surface tests for /api/notifications. These exercise:
///   • Happy path: notification persists in <c>Created</c> with idempotency key.
///   • Suppression: pre-seeded suppression row blocks dispatch (<c>Suppressed</c>).
///   • Idempotency: replaying the SAME body-level idempotency key yields the
///     same Notification.Id (handler-level dedup, not the global HTTP
///     middleware which 409s on header replay).
/// </summary>
[Collection("Notifications Integration")]
public sealed class NotificationsApiTests : IAsyncLifetime
{
    private readonly NotificationsWebAppFactory _factory;

    public NotificationsApiTests(NotificationsWebAppFactory factory)
    {
        // Shared factory via [Collection] — no per-test mock needed. API tests
        // assert HTTP surface + DB state; the dispatch pipeline runs through
        // the no-IEmailProvider path (gateway logs + marks Failed), which is
        // fine because these tests don't assert dispatch outcomes — Pipeline
        // tests cover that with per-test mock injection.
        _factory = factory;
        // NOTE: don't eagerly build the client in the ctor — CreateClient()
        // forces a host build which throws on the duplicate AddMassTransit
        // (see file-level TODO). Each [Fact] lazily creates the client.
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Send_creates_notification_with_status_Created()
    {
        await EnsureBootedAsync();
        var client = _factory.CreateClient();

        var recipient = $"happy-{Guid.NewGuid():N}@test.invalid";
        var resp = await client.PostAsJsonAsync("/api/notifications", new
        {
            userId = (string?)null,
            recipient,
            channel = (int)NotificationChannel.Email,
            templateId = "tpl-welcome",
            priority = (int)NotificationPriority.Normal,
            variables = new Dictionary<string, object>(),
            idempotencyKey = (string?)null,
        });

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        var notificationId = await ExtractIdAsync(resp);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var row = await db.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notificationId);
        row.Should().NotBeNull("the notification row should be persisted by the time the response returns");
        row!.Recipient.Should().Be(recipient);
        row.IdempotencyKey.Should().NotBeNullOrWhiteSpace("handler always generates a key even when none is supplied");
        row.Status.Should().BeOneOf(
            NotificationStatus.Created,
            NotificationStatus.Rendering,
            NotificationStatus.Queued,
            NotificationStatus.Sent,
            NotificationStatus.Failed);
    }

    [Fact]
    public async Task Send_with_suppressed_recipient_returns_Suppressed()
    {
        await EnsureBootedAsync();
        var client = _factory.CreateClient();

        var recipient = $"suppressed-{Guid.NewGuid():N}@test.invalid";

        await using (var seedScope = _factory.Services.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var hash = SuppressionService.HashRecipient(recipient, NotificationChannel.Email);
            var suppression = SuppressionFactoryAccessor.Create(hash, NotificationChannel.Email, "test-bounce", null);
            db.SuppressionList.Add(suppression);
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsJsonAsync("/api/notifications", new
        {
            userId = (string?)null,
            recipient,
            channel = (int)NotificationChannel.Email,
            templateId = "tpl-welcome",
            priority = (int)NotificationPriority.Normal,
            variables = new Dictionary<string, object>(),
            idempotencyKey = (string?)null,
        });

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        var notificationId = await ExtractIdAsync(resp);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var row = await db2.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.Id == notificationId);
        row.Should().NotBeNull();
        row!.Status.Should().Be(NotificationStatus.Suppressed,
            "pre-seeded suppression row must short-circuit the handler before the Created state");
    }

    [Fact]
    public async Task Send_idempotency_returns_existing_id_on_replay()
    {
        await EnsureBootedAsync();
        var client = _factory.CreateClient();

        var recipient = $"idem-{Guid.NewGuid():N}@test.invalid";
        // Body-level IdempotencyKey is what the SendNotificationCommandHandler
        // hashes (along with userId/templateId/recipient). The HTTP-layer
        // middleware reads X-Idempotency-Key and 409s — that's a different
        // path. This test exercises the handler-level dedup.
        var clientKey = "stable-" + Guid.NewGuid().ToString("N");

        var body = new
        {
            userId = (string?)null,
            recipient,
            channel = (int)NotificationChannel.Email,
            templateId = "tpl-welcome",
            priority = (int)NotificationPriority.Normal,
            variables = new Dictionary<string, object>(),
            idempotencyKey = clientKey,
        };

        var first = await client.PostAsJsonAsync("/api/notifications", body);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        var firstId = await ExtractIdAsync(first);

        var second = await client.PostAsJsonAsync("/api/notifications", body);
        second.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        var secondId = await ExtractIdAsync(second);

        secondId.Should().Be(firstId, "replaying the same idempotency key must dedupe at the handler layer");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var rowCount = await db.Notifications.AsNoTracking().CountAsync(n => n.Recipient == recipient);
        rowCount.Should().Be(1, "the second POST must not insert a duplicate notification row");
    }

    private async Task EnsureBootedAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    private static async Task<Guid> ExtractIdAsync(HttpResponseMessage resp)
    {
        var raw = (await resp.Content.ReadAsStringAsync()).Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"'))
        {
            raw = raw[1..^1];
        }
        return Guid.Parse(raw);
    }
}

/// <summary>
/// SuppressionFactory is internal to Notifications.Application; this shim
/// re-exposes it via reflection because the L4 test assembly isn't on its
/// InternalsVisibleTo list (Notifications.Unit is, the integration project
/// isn't, and adding it would touch a non-test file).
/// </summary>
internal static class SuppressionFactoryAccessor
{
    public static Haworks.Notifications.Domain.Entities.Suppression Create(
        string recipientHash,
        NotificationChannel channel,
        string reason,
        string? sourceEventId)
    {
        var type = typeof(Haworks.Notifications.Application.Commands.SendNotificationCommand)
            .Assembly
            .GetType("Haworks.Notifications.Application.Suppression.SuppressionFactory")
            ?? throw new InvalidOperationException("SuppressionFactory not found");
        var method = type.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SuppressionFactory.Create not found");
        return (Haworks.Notifications.Domain.Entities.Suppression)method.Invoke(null, new object?[] { recipientHash, channel, reason, sourceEventId })!;
    }
}
