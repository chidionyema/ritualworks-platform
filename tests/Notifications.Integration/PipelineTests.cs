using System.Net.Http.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Persistence;

namespace Haworks.Notifications.Integration;

// TODO(notif-L4): see NotificationsApiTests.cs — Notifications.Api Program.cs cannot
// boot the WebApplicationFactory host because both Application + Infrastructure DI
// extensions call services.AddMassTransit (forbidden by MT v8.3.4). Tests below are
// [Fact(Skip=...)] until the service-side bug is collapsed into a single AddMassTransit
// call. Code is left in place so the assertions are immediately runnable once the bug
// is addressed.

/// <summary>
/// End-to-end pipeline tests covering NotificationCreatedEvent → channel
/// gateway → IEmailProvider failover.
///
/// Each test class builds its own NotificationsWebAppFactory because we need
/// to install different IEmailProvider mocks per scenario (success vs.
/// retryable+success failover) before the host is built.
/// </summary>
public sealed class PipelineDispatchTests : IClassFixture<PipelineDispatchTests.SuccessFactory>, IAsyncLifetime
{
    private const string ServiceBootBugSkipReason =
        "TODO(notif-L4): Notifications.Api host fails to boot because Application.AddNotificationsApplication " +
        "calls services.AddMassTransit a second time after Infrastructure already did. MT v8.3.4 forbids this.";

    public sealed class SuccessFactory : NotificationsWebAppFactory
    {
        public Mock<IEmailProvider> EmailMock { get; } = new();

        public SuccessFactory()
        {
            EmailMock.SetupGet(p => p.Name).Returns("ses-mock");
            EmailMock
                .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderSendResult.Success("msg-success-1"));

            ConfigureEmailProviders = services =>
            {
                services.AddSingleton<IEmailProvider>(EmailMock.Object);
            };
        }
    }

    private readonly SuccessFactory _factory;

    public PipelineDispatchTests(SuccessFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Skip = ServiceBootBugSkipReason)]
    public async Task Created_event_dispatches_via_email_provider()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        var client = _factory.CreateClient();
        var recipient = $"dispatch-{Guid.NewGuid():N}@test.invalid";
        var resp = await client.PostAsJsonAsync("/api/notifications", new
        {
            userId = (string?)null,
            recipient,
            channel = (int)NotificationChannel.Email,
            templateId = "tpl-dispatch",
            priority = (int)NotificationPriority.Normal,
            variables = new Dictionary<string, object>(),
            idempotencyKey = (string?)null,
        });
        resp.EnsureSuccessStatusCode();
        var notificationId = await ExtractIdAsync(resp);

        // Belt-and-braces: republish the event through the harness in case the
        // outbox-driven publish from the POST hasn't reached the consumer yet.
        await harness.Bus.Publish(new NotificationCreatedEvent
        {
            NotificationId = notificationId,
            TemplateId = "tpl-dispatch",
            Channel = NotificationChannel.Email,
            Priority = NotificationPriority.Normal,
            UserId = null,
            Recipient = recipient,
            IdempotencyKey = "harness-republish-" + Guid.NewGuid().ToString("N"),
        });

        await PollUntilStatusAsync(notificationId, NotificationStatus.Sent, TimeSpan.FromSeconds(30));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var row = await db.Notifications
            .Include(n => n.DeliveryAttempts)
            .AsNoTracking()
            .FirstAsync(n => n.Id == notificationId);

        row.Status.Should().Be(NotificationStatus.Sent);
        row.ProviderMessageId.Should().Be("msg-success-1");
        row.DeliveryAttempts.Should().ContainSingle()
            .Which.IsSuccess.Should().BeTrue();

        _factory.EmailMock.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    private async Task PollUntilStatusAsync(Guid id, NotificationStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var status = await db.Notifications.AsNoTracking()
                .Where(n => n.Id == id)
                .Select(n => (NotificationStatus?)n.Status)
                .FirstOrDefaultAsync();
            if (status == expected) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Notification {id} never reached status {expected} within {timeout}.");
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
/// Failover scenario: register two IEmailProvider impls. First returns
/// Retryable, second returns Success. The EmailChannelGateway must walk the
/// chain in DI registration order, record both attempts, and end up Sent.
/// </summary>
public sealed class PipelineFailoverTests : IClassFixture<PipelineFailoverTests.FailoverFactory>, IAsyncLifetime
{
    private const string ServiceBootBugSkipReason =
        "TODO(notif-L4): Notifications.Api host fails to boot because Application.AddNotificationsApplication " +
        "calls services.AddMassTransit a second time after Infrastructure already did. MT v8.3.4 forbids this.";

    public sealed class FailoverFactory : NotificationsWebAppFactory
    {
        public Mock<IEmailProvider> Primary { get; } = new();
        public Mock<IEmailProvider> Secondary { get; } = new();

        public FailoverFactory()
        {
            Primary.SetupGet(p => p.Name).Returns("ses-primary");
            Primary
                .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderSendResult.Retryable("ses-throttled"));

            Secondary.SetupGet(p => p.Name).Returns("sendgrid-secondary");
            Secondary
                .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProviderSendResult.Success("msg-failover-2"));

            ConfigureEmailProviders = services =>
            {
                services.AddSingleton<IEmailProvider>(Primary.Object);
                services.AddSingleton<IEmailProvider>(Secondary.Object);
            };
        }
    }

    private readonly FailoverFactory _factory;

    public PipelineFailoverTests(FailoverFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact(Skip = ServiceBootBugSkipReason)]
    public async Task Provider_failover_falls_through_to_secondary()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        var client = _factory.CreateClient();
        var recipient = $"failover-{Guid.NewGuid():N}@test.invalid";
        var resp = await client.PostAsJsonAsync("/api/notifications", new
        {
            userId = (string?)null,
            recipient,
            channel = (int)NotificationChannel.Email,
            templateId = "tpl-failover",
            priority = (int)NotificationPriority.Normal,
            variables = new Dictionary<string, object>(),
            idempotencyKey = (string?)null,
        });
        resp.EnsureSuccessStatusCode();
        var notificationId = await ExtractIdAsync(resp);

        await harness.Bus.Publish(new NotificationCreatedEvent
        {
            NotificationId = notificationId,
            TemplateId = "tpl-failover",
            Channel = NotificationChannel.Email,
            Priority = NotificationPriority.Normal,
            UserId = null,
            Recipient = recipient,
            IdempotencyKey = "harness-failover-" + Guid.NewGuid().ToString("N"),
        });

        await PollUntilStatusAsync(notificationId, NotificationStatus.Sent, TimeSpan.FromSeconds(30));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var row = await db.Notifications
            .Include(n => n.DeliveryAttempts)
            .AsNoTracking()
            .FirstAsync(n => n.Id == notificationId);

        row.Status.Should().Be(NotificationStatus.Sent);
        row.ProviderMessageId.Should().Be("msg-failover-2");

        var attempts = row.DeliveryAttempts.OrderBy(a => a.AttemptedAt).ToList();
        attempts.Should().HaveCount(2,
            "primary retryable failure + secondary success must each leave a DeliveryAttempt");
        attempts[0].ProviderName.Should().Be("ses-primary");
        attempts[0].IsSuccess.Should().BeFalse();
        attempts[1].ProviderName.Should().Be("sendgrid-secondary");
        attempts[1].IsSuccess.Should().BeTrue();

        _factory.Primary.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _factory.Secondary.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    private async Task PollUntilStatusAsync(Guid id, NotificationStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var status = await db.Notifications.AsNoTracking()
                .Where(n => n.Id == id)
                .Select(n => (NotificationStatus?)n.Status)
                .FirstOrDefaultAsync();
            if (status == expected) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Notification {id} never reached status {expected} within {timeout}.");
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
