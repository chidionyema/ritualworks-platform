using FluentAssertions;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Domain.ValueObjects;
using Xunit;

namespace Haworks.Notifications.Unit.Domain;

/// <summary>
/// L1.A — Notification aggregate state-machine guards. Mirrors the
/// OrderTests.cs structure; each invalid transition must throw
/// InvalidOperationException with the from/to context.
/// </summary>
public class NotificationTests
{
    private const string DefaultRecipient = "user@example.com";
    private const string DefaultTemplateId = "order.shipped";
    private const string DefaultIdempotencyKey = "sha256-abc123";
    private const NotificationChannel DefaultChannel = NotificationChannel.Email;

    private static Notification NewCreated() =>
        Notification.Create(DefaultRecipient, DefaultChannel, DefaultTemplateId, DefaultIdempotencyKey);

    private static Notification NewSent()
    {
        var n = NewCreated();
        n.MarkRendering();
        n.MarkQueued();
        n.MarkSent("provider-msg-1");
        return n;
    }

    #region Factory

    [Fact]
    public void Create_WithValidParameters_ReturnsCreatedNotification()
    {
        var n = Notification.Create(
            recipient: DefaultRecipient,
            channel: NotificationChannel.Sms,
            templateId: "otp.verify",
            idempotencyKey: DefaultIdempotencyKey,
            userId: "user-123",
            priority: NotificationPriority.High);

        n.Id.Should().NotBeEmpty();
        n.Recipient.Should().Be(DefaultRecipient);
        n.Channel.Should().Be(NotificationChannel.Sms);
        n.TemplateId.Should().Be("otp.verify");
        n.IdempotencyKey.Should().Be(DefaultIdempotencyKey);
        n.UserId.Should().Be("user-123");
        n.Priority.Should().Be(NotificationPriority.High);
        n.Status.Should().Be(NotificationStatus.Created);
        n.SentAt.Should().BeNull();
        n.DeliveredAt.Should().BeNull();
        n.DeliveryAttempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankRecipient_Throws(string? recipient)
    {
        var act = () => Notification.Create(recipient!, DefaultChannel, DefaultTemplateId, DefaultIdempotencyKey);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankTemplateId_Throws(string? templateId)
    {
        var act = () => Notification.Create(DefaultRecipient, DefaultChannel, templateId!, DefaultIdempotencyKey);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankIdempotencyKey_Throws(string? key)
    {
        var act = () => Notification.Create(DefaultRecipient, DefaultChannel, DefaultTemplateId, key!);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Happy-path transitions

    [Fact]
    public void HappyPath_CreatedRenderingQueuedSentDelivered_AdvancesStatus()
    {
        var n = NewCreated();
        n.MarkRendering();
        n.Status.Should().Be(NotificationStatus.Rendering);

        n.MarkQueued();
        n.Status.Should().Be(NotificationStatus.Queued);

        n.MarkSent("pmid-1");
        n.Status.Should().Be(NotificationStatus.Sent);
        n.ProviderMessageId.Should().Be("pmid-1");
        n.SentAt.Should().NotBeNull();

        n.MarkDelivered();
        n.Status.Should().Be(NotificationStatus.Delivered);
        n.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkSent_Parameterless_TransitionsButLeavesProviderMessageIdNull()
    {
        var n = NewCreated();
        n.MarkRendering();
        n.MarkQueued();
        n.MarkSent();
        n.Status.Should().Be(NotificationStatus.Sent);
        n.ProviderMessageId.Should().BeNull();
        n.SentAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkComplained_FromDelivered_Transitions()
    {
        var n = NewSent();
        n.MarkDelivered();
        n.MarkComplained();
        n.Status.Should().Be(NotificationStatus.Complained);
    }

    [Fact]
    public void MarkBounced_FromSent_Transitions()
    {
        var n = NewSent();
        n.MarkBounced("550 mailbox unavailable");
        n.Status.Should().Be(NotificationStatus.Bounced);
        n.ErrorMessage.Should().Be("550 mailbox unavailable");
    }

    [Fact]
    public void MarkFailed_FromAnyNonTerminalState_Transitions()
    {
        var n = NewCreated();
        n.MarkFailed("renderer crashed");
        n.Status.Should().Be(NotificationStatus.Failed);
        n.ErrorMessage.Should().Be("renderer crashed");
    }

    #endregion

    #region Invalid transitions

    [Fact]
    public void MarkQueued_FromCreated_Throws()
    {
        var n = NewCreated();
        var act = () => n.MarkQueued();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MarkQueued*Created*");
    }

    [Fact]
    public void MarkSent_FromCreated_Throws()
    {
        var n = NewCreated();
        var act = () => n.MarkSent("pmid");
        act.Should().Throw<InvalidOperationException>().WithMessage("*MarkSent*Created*");
    }

    [Fact]
    public void MarkDelivered_FromCreated_Throws()
    {
        var n = NewCreated();
        var act = () => n.MarkDelivered();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MarkDelivered*Created*");
    }

    [Fact]
    public void MarkBounced_FromCreated_Throws()
    {
        var n = NewCreated();
        var act = () => n.MarkBounced("nope");
        act.Should().Throw<InvalidOperationException>().WithMessage("*MarkBounced*Created*");
    }

    [Fact]
    public void MarkComplained_FromSent_Throws_RequiresDelivered()
    {
        var n = NewSent();
        var act = () => n.MarkComplained();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MarkComplained*Sent*");
    }

    [Fact]
    public void MarkRendering_Twice_Throws()
    {
        var n = NewCreated();
        n.MarkRendering();
        var act = () => n.MarkRendering();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSent_AfterDelivered_Throws()
    {
        var n = NewSent();
        n.MarkDelivered();
        var act = () => n.MarkSent("pmid-2");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_FromTerminalDelivered_Throws()
    {
        var n = NewSent();
        n.MarkDelivered();
        var act = () => n.MarkFailed("late failure");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_FromTerminalBounced_Throws()
    {
        var n = NewSent();
        n.MarkBounced("hard bounce");
        var act = () => n.MarkFailed("late failure");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_FromTerminalFailed_Throws()
    {
        var n = NewCreated();
        n.MarkFailed("first");
        var act = () => n.MarkFailed("second");
        act.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region Argument guards

    [Fact]
    public void MarkSent_BlankProviderMessageId_Throws()
    {
        var n = NewCreated();
        n.MarkRendering();
        n.MarkQueued();
        var act = () => n.MarkSent("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkBounced_BlankReason_Throws()
    {
        var n = NewSent();
        var act = () => n.MarkBounced("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkFailed_BlankReason_Throws()
    {
        var n = NewCreated();
        var act = () => n.MarkFailed("");
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region RecordAttempt

    [Fact]
    public void RecordAttempt_WithDeliveryAttempt_AppendsToCollection()
    {
        var n = NewCreated();
        var attempt = new DeliveryAttempt(
            AttemptedAt: DateTime.UtcNow,
            ProviderName: "Sendgrid",
            ProviderMessageId: "pmid",
            IsSuccess: true,
            ErrorMessage: null);

        n.RecordAttempt(attempt);

        n.DeliveryAttempts.Should().ContainSingle()
            .Which.ProviderName.Should().Be("Sendgrid");
    }

    [Fact]
    public void RecordAttempt_PrimitiveOverload_DerivesSuccessFrom2xxStatus()
    {
        var n = NewCreated();
        n.RecordAttempt("Sendgrid", statusCode: 202, errorMessage: null, providerMessageId: "pmid");

        n.DeliveryAttempts.Should().ContainSingle()
            .Which.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RecordAttempt_PrimitiveOverload_NonSuccessStatus_MarksFailure()
    {
        var n = NewCreated();
        n.RecordAttempt("Sendgrid", statusCode: 500, errorMessage: "boom", providerMessageId: null);

        var attempt = n.DeliveryAttempts.Should().ContainSingle().Subject;
        attempt.IsSuccess.Should().BeFalse();
        attempt.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public void RecordAttempt_PrimitiveOverload_NullStatus_MarksFailure()
    {
        var n = NewCreated();
        n.RecordAttempt("Sendgrid", statusCode: null, errorMessage: "timeout", providerMessageId: null);

        n.DeliveryAttempts.Should().ContainSingle()
            .Which.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void RecordAttempt_AfterTerminal_Throws()
    {
        var n = NewSent();
        n.MarkBounced("550");
        var act = () => n.RecordAttempt("Sendgrid", 500, "post-terminal", null);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RecordAttempt_BlankProvider_Throws()
    {
        var n = NewCreated();
        var act = () => n.RecordAttempt("  ", 200, null, "pmid");
        act.Should().Throw<ArgumentException>();
    }

    #endregion
}
