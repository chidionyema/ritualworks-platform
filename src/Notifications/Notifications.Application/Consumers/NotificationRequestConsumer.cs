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
    ISmsChannelGateway smsGateway,
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
        // MassTransit EF Outbox commits automatically

        // Step 2: render via the selected template. Renderer/selector may not
        // yet be implemented (L1.B); fall back to the persisted Subject/Body
        // verbatim so the dispatch surface is still exercisable.
        string renderedSubject = notification.Subject;
        string renderedBody = notification.Body;
        try
        {
            var template = await templateSelector.SelectAsync(
                notification.TemplateId, DefaultLocale, notification.Channel);

            var variables = notification.Variables ?? new Dictionary<string, object>();

            if (template is not null)
            {
                renderedSubject = await templateRenderer.RenderAsync(template.SubjectTemplate, variables);
                renderedBody = await templateRenderer.RenderAsync(template.BodyTemplate, variables);
            }
            else
            {
                // No active template row for this templateId/locale/channel.
                // Fall through with the persisted Subject/Body so the dispatch
                // path remains exercisable (same intent as the NotImplementedException
                // catch below).
                logger.LogWarning(
                    "No active template found for templateId={TemplateId}, channel={Channel}; dispatching {NotificationId} with persisted body",
                    notification.TemplateId, notification.Channel, notification.Id);
            }
        }
        catch (NotImplementedException)
        {
            // L1.B template surface still stubbed — proceed with persisted
            // Subject/Body so the email path is still observable in dev/test.
            logger.LogWarning(
                "Template surface not yet implemented; dispatching {NotificationId} with persisted body",
                notification.Id);
        }

        notification.SetRenderedContent(renderedSubject, renderedBody);
        // MassTransit EF Outbox commits automatically

        notification.MarkQueued();

        // Persist Queued status before external call so a re-read after rollback
        // can detect the attempt. This is a best-effort guard — see limitation below.
        await repository.SaveChangesAsync(ct);

        // Step 4: dispatch via the channel-appropriate gateway. The gateway
        // mutates the aggregate (RecordAttempt + MarkSent or MarkFailed).
        //
        // KNOWN LIMITATION: The external send happens inside the outbox transaction.
        // If send succeeds but the subsequent outbox commit fails, the notification is
        // delivered but status rolls back to Queued, causing a duplicate on retry.
        // The Queued status guard above (persisted before send) and the Created status
        // guard at the top provide partial protection, but the fundamental fix requires
        // splitting into two consumers: (1) render+persist as Queued+publish SendNotificationCommand,
        // (2) pick up SendNotificationCommand, do the actual send, update status.
        // Channel gateways should also be made idempotent (provider-side dedup via ProviderMessageId).
        switch (notification.Channel)
        {
            case NotificationChannel.Email:
                await emailGateway.SendAsync(notification, ct);
                break;
            case NotificationChannel.Sms:
                await smsGateway.SendAsync(notification, ct);
                break;
            case NotificationChannel.Push:
                await pushGateway.SendAsync(notification, ct);
                break;
            default:
                // Unknown channel — mark failed explicitly so the aggregate
                // doesn't stay stuck in Queued.
                notification.RecordAttempt(new DeliveryAttempt(
                    AttemptedAt: DateTime.UtcNow,
                    ProviderName: "n/a",
                    ProviderMessageId: null,
                    IsSuccess: false,
                    ErrorMessage: $"unknown channel {notification.Channel}"));
                notification.MarkFailed($"unknown channel {notification.Channel}");
                break;
        }

        // MassTransit EF Outbox commits automatically

        logger.LogInformation(
            "Notification {NotificationId} dispatch complete: status={Status}, providerMessageId={ProviderMessageId}",
            notification.Id, notification.Status, notification.ProviderMessageId);
    }
}
