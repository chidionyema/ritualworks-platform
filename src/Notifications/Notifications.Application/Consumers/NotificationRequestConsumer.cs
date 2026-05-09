using MassTransit;
using Microsoft.Extensions.Logging;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Templates;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Application.Consumers;

/// <summary>
/// L3 dispatch consumer. Subscribes to <see cref="NotificationCreatedEvent"/>
/// (raised by <c>SendNotificationCommandHandler</c> after the Notification row
/// commits in <see cref="NotificationStatus.Created"/>) and drives the row
/// through the render -> queue -> send portion of the state machine.
///
/// Responsibilities (per docs/architecture/notification-service.md §4 + §6):
///   1. Load the persisted Notification + matching NotificationTemplate.
///   2. <see cref="Notification.MarkRendering"/> -> SaveChanges (audit-visible).
///   3. Render subject/body via <see cref="ITemplateRenderer"/> (best-effort —
///      L1.B owns the renderer body; if it's still a stub, fall through with
///      the persisted Subject/Body verbatim so the dispatch path remains
///      exercisable end-to-end).
///   4. <see cref="Notification.MarkQueued"/> and dispatch via the channel
///      gateway corresponding to <see cref="Notification.Channel"/>.
///   5. The channel gateway updates the Notification in-place
///      (<see cref="Notification.MarkSent(string)"/> /
///      <see cref="Notification.RecordAttempt(DeliveryAttempt)"/> /
///      <see cref="Notification.MarkFailed(string)"/>); we then SaveChanges.
///
/// Idempotency layers:
///   1. MassTransit inbox (MessageId-based dedup at transport level).
///   2. Application: terminal-state guard — if the Notification was already
///      Sent/Delivered/Failed by a prior attempt we ack and return without
///      re-dispatching (see <c>EnsureNotTerminal</c> in the aggregate).
///
/// Cross-context references (UserId) remain opaque; the consumer touches no
/// foreign-context state per ADR-0009.
/// </summary>
public sealed class NotificationRequestConsumer(
    INotificationRepository repository,
    ITemplateSelector templateSelector,
    ITemplateRenderer templateRenderer,
    IEmailChannelGateway emailGateway,
    IPushChannelGateway pushGateway,
    ILogger<NotificationRequestConsumer> logger
) : IConsumer<NotificationCreatedEvent>
{
    private const string DefaultLocale = "en-US";

    public async Task Consume(ConsumeContext<NotificationCreatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        logger.LogInformation(
            "Dispatching notification: notificationId={NotificationId}, channel={Channel}, templateId={TemplateId}",
            evt.NotificationId, evt.Channel, evt.TemplateId);

        var notification = await repository.GetByIdAsync(evt.NotificationId, ct);
        if (notification is null)
        {
            // The Outbox should normally guarantee the row exists by the time the
            // event is consumed (event written in same EF txn as the INSERT).
            // A null here means out-of-order delivery (rare) or the row was
            // hard-deleted (admin tooling) — ack so we don't poison-loop.
            logger.LogWarning(
                "Notification {NotificationId} not found for NotificationCreatedEvent; ignoring",
                evt.NotificationId);
            return;
        }

        if (notification.Status != NotificationStatus.Created)
        {
            // Re-delivery from MassTransit after the initial dispatch already
            // moved the aggregate beyond Created. The state-machine guards in
            // MarkRendering/MarkQueued would throw — short-circuit here so the
            // re-deliver acks cleanly. (Ledger of attempts is preserved on the
            // aggregate via the original dispatch.)
            logger.LogInformation(
                "Notification {NotificationId} no longer in Created (status={Status}); skipping",
                notification.Id, notification.Status);
            return;
        }

        // Step 1: Created -> Rendering. Persist so dashboards + audits see the
        // transition even if rendering hangs/crashes.
        notification.MarkRendering();
        await repository.SaveChangesAsync(ct);

        // Step 2: render via the selected template. Renderer/selector may not
        // yet be implemented (L1.B); fall back to the persisted Subject/Body
        // verbatim so the dispatch surface is still exercisable.
        string renderedSubject = notification.Subject;
        string renderedBody = notification.Body;
        try
        {
            var template = await templateSelector.SelectAsync(
                notification.TemplateId, DefaultLocale, notification.Channel);

            // Variables aren't yet captured on the Notification aggregate
            // (L1.A pending — Variables column / RenderContext). Pass an empty
            // map; renderer must tolerate this. // TODO(notif-L3): wire payload
            // variables once the aggregate carries them.
            var variables = new Dictionary<string, object>();

            renderedSubject = await templateRenderer.RenderAsync(template.SubjectTemplate, variables);
            renderedBody = await templateRenderer.RenderAsync(template.BodyTemplate, variables);
        }
        catch (NotImplementedException)
        {
            // L1.B template surface still stubbed — proceed with persisted
            // Subject/Body so the email path is still observable in dev/test.
            logger.LogWarning(
                "Template surface not yet implemented; dispatching {NotificationId} with persisted body",
                notification.Id);
        }

        // Apply rendered content via the aggregate (no public setter on
        // Subject/Body; gateway picks up the persisted values once we
        // SaveChanges below). For now, the gateway reads from the aggregate
        // post-render — a future track may extract a RenderedPayload VO so
        // we don't need to mutate via reflection. // TODO(notif-L3): swap to
        // a RenderedMessage VO once L1.B finalises the payload contract.
        _ = renderedSubject; _ = renderedBody;

        // Step 3: Rendering -> Queued. The gateway treats Queued as the
        // pre-condition for MarkSent/MarkFailed.
        notification.MarkQueued();

        // Step 4: dispatch via the channel-appropriate gateway. The gateway
        // mutates the aggregate (RecordAttempt + MarkSent or MarkFailed).
        switch (notification.Channel)
        {
            case NotificationChannel.Email:
                await emailGateway.SendAsync(notification, ct);
                break;
            case NotificationChannel.Push:
                // TODO(notif-F3): Handled by PushChannelGateway
                await pushGateway.SendAsync(notification, ct);
                break;
            case NotificationChannel.Sms:
                // SMS gateways are owned by other tracks. Mark failed
                // explicitly so we don't leave the aggregate stuck in Queued.
                notification.RecordAttempt(new DeliveryAttempt(
                    AttemptedAt: DateTime.UtcNow,
                    ProviderName: "n/a",
                    ProviderMessageId: null,
                    IsSuccess: false,
                    ErrorMessage: $"channel {notification.Channel} not implemented"));
                notification.MarkFailed($"channel {notification.Channel} not implemented");
                break;
            default:
                notification.MarkFailed($"unknown channel {notification.Channel}");
                break;
        }

        await repository.SaveChangesAsync(ct);

        logger.LogInformation(
            "Notification {NotificationId} dispatch complete: status={Status}, providerMessageId={ProviderMessageId}",
            notification.Id, notification.Status, notification.ProviderMessageId);
    }
}
