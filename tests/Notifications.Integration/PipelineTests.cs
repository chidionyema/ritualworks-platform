using System.Net.Http.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Infrastructure.Persistence;

namespace Haworks.Notifications.Integration;

/// <summary>
/// End-to-end pipeline tests covering NotificationCreatedEvent → channel
/// gateway → IEmailProvider failover.
///
/// Uses the SHARED <see cref="NotificationsWebAppFactory"/> via the
/// <c>"Notifications Integration"</c> collection. Per-test email-provider
/// mocks are injected via <c>WithWebHostBuilder + ConfigureTestServices</c>
/// rather than subclassing the factory — see .claude/rules/testing.md
/// "Minimise WebApplicationFactory fixture count".
/// </summary>
[Collection("Notifications Integration")]
public sealed class PipelineTests(NotificationsWebAppFactory factory)
{
    private readonly NotificationsWebAppFactory _factory = factory;

    [Fact]
    public async Task Created_event_dispatches_via_email_provider()
    {
        await _factory.EnsureSchemaAsync();

        var emailMock = new Mock<IEmailProvider>();
        emailMock.SetupGet(p => p.Name).Returns("ses-mock");
        emailMock
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Success("msg-success-1"));

        using var scopedFactory = WithEmailProviders(emailMock.Object);

        var harness = scopedFactory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        var client = scopedFactory.CreateClient();
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

        // Belt-and-braces republish in case the outbox-driven publish from the
        // POST hasn't reached the consumer yet.
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

        await PollUntilStatusAsync(scopedFactory, notificationId, NotificationStatus.Sent, TimeSpan.FromSeconds(30));

        await using var scope = scopedFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var row = await db.Notifications
            .Include(n => n.DeliveryAttempts)
            .AsNoTracking()
            .FirstAsync(n => n.Id == notificationId);

        row.Status.Should().Be(NotificationStatus.Sent);
        row.ProviderMessageId.Should().Be("msg-success-1");
        row.DeliveryAttempts.Should().ContainSingle()
            .Which.IsSuccess.Should().BeTrue();

        emailMock.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Provider_failover_falls_through_to_secondary()
    {
        await _factory.EnsureSchemaAsync();

        var primary = new Mock<IEmailProvider>();
        primary.SetupGet(p => p.Name).Returns("ses-primary");
        primary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Retryable("ses-throttled"));

        var secondary = new Mock<IEmailProvider>();
        secondary.SetupGet(p => p.Name).Returns("sendgrid-secondary");
        secondary
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProviderSendResult.Success("msg-failover-2"));

        using var scopedFactory = WithEmailProviders(primary.Object, secondary.Object);

        var harness = scopedFactory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();

        var client = scopedFactory.CreateClient();
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

        await PollUntilStatusAsync(scopedFactory, notificationId, NotificationStatus.Sent, TimeSpan.FromSeconds(30));

        await using var scope = scopedFactory.Services.CreateAsyncScope();
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

        primary.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        secondary.Verify(
            p => p.SendAsync(recipient, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Returns a scoped factory variant with the supplied email providers
    /// registered (replacing whatever the parent had). Per-test scope —
    /// disposed at end of test, parent factory unaffected.
    /// </summary>
    private WebApplicationFactory<Program> WithEmailProviders(params IEmailProvider[] providers) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailProvider>();
                foreach (var p in providers)
                {
                    services.AddSingleton(p);
                }
            });
        });

    private static async Task PollUntilStatusAsync(
        WebApplicationFactory<Program> factory,
        Guid id, NotificationStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
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
