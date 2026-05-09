using FluentAssertions;
using Haworks.Notifications.Application.Suppression;
using Haworks.Notifications.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Notifications.Unit.Suppression;

public class SuppressionTests
{
    private readonly Mock<ISuppressionRepository> _repo = new(MockBehavior.Strict);

    private SuppressionService BuildSut() =>
        new(_repo.Object, NullLogger<SuppressionService>.Instance);

    // ─── HashRecipient ────────────────────────────────────────────────────

    [Fact]
    public void HashRecipient_EmailIsCaseInsensitive()
    {
        var lower = SuppressionService.HashRecipient("user@example.com", NotificationChannel.Email);
        var mixed = SuppressionService.HashRecipient("UsEr@Example.COM", NotificationChannel.Email);
        var padded = SuppressionService.HashRecipient("  user@example.com  ", NotificationChannel.Email);

        mixed.Should().Be(lower);
        padded.Should().Be(lower);
    }

    [Fact]
    public void HashRecipient_DifferentEmail_DifferentHash()
    {
        var a = SuppressionService.HashRecipient("alice@example.com", NotificationChannel.Email);
        var b = SuppressionService.HashRecipient("bob@example.com", NotificationChannel.Email);

        a.Should().NotBe(b);
    }

    [Fact]
    public void HashRecipient_IsLowercaseHex64()
    {
        var hash = SuppressionService.HashRecipient("user@example.com", NotificationChannel.Email);

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashRecipient_PhoneNormalizesFormatting()
    {
        var raw = SuppressionService.HashRecipient("+14155552671", NotificationChannel.Sms);
        var pretty = SuppressionService.HashRecipient("+1 (415) 555-2671", NotificationChannel.Sms);
        var withSpaces = SuppressionService.HashRecipient(" +1-415-555-2671 ", NotificationChannel.Sms);

        pretty.Should().Be(raw);
        withSpaces.Should().Be(raw);
    }

    [Fact]
    public void HashRecipient_PushTokenIsCaseSensitive()
    {
        // Device tokens are opaque, case-sensitive — matching MUST be exact.
        var lower = SuppressionService.HashRecipient("abc123token", NotificationChannel.Push);
        var upper = SuppressionService.HashRecipient("ABC123TOKEN", NotificationChannel.Push);

        upper.Should().NotBe(lower);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HashRecipient_BlankInput_Throws(string? recipient)
    {
        var act = () => SuppressionService.HashRecipient(recipient!, NotificationChannel.Email);

        act.Should().Throw<ArgumentException>();
    }

    // ─── IsSuppressedAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task IsSuppressedAsync_HashHit_BlocksRecipient()
    {
        var expectedHash = SuppressionService.HashRecipient("user@example.com", NotificationChannel.Email);
        _repo.Setup(r => r.ExistsAsync(expectedHash, NotificationChannel.Email))
             .ReturnsAsync(true);

        var sut = BuildSut();

        var blocked = await sut.IsSuppressedAsync("USER@example.com", NotificationChannel.Email, CancellationToken.None);

        blocked.Should().BeTrue();
        _repo.VerifyAll();
    }

    [Fact]
    public async Task IsSuppressedAsync_NoHit_AllowsRecipient()
    {
        _repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), NotificationChannel.Email))
             .ReturnsAsync(false);

        var sut = BuildSut();

        var blocked = await sut.IsSuppressedAsync("user@example.com", NotificationChannel.Email, CancellationToken.None);

        blocked.Should().BeFalse();
    }

    // ─── AddAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_PersistsHashedRecipient()
    {
        Haworks.Notifications.Domain.Entities.Suppression? captured = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<Haworks.Notifications.Domain.Entities.Suppression>()))
             .Callback<Haworks.Notifications.Domain.Entities.Suppression>(s => captured = s)
             .Returns(Task.CompletedTask);

        var sut = BuildSut();

        await sut.AddAsync("User@Example.com", NotificationChannel.Email, "hard_bounce", "evt-123", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.RecipientHash.Should().Be(
            SuppressionService.HashRecipient("user@example.com", NotificationChannel.Email));
        captured.Channel.Should().Be(NotificationChannel.Email);
        captured.Reason.Should().Be("hard_bounce");
        captured.SourceEventId.Should().Be("evt-123");
    }

    [Fact]
    public async Task AddAsync_RepositoryNoOpOnDuplicate_DoesNotThrow()
    {
        // The repository's AddAsync is documented as idempotent — duplicate
        // adds are silently ignored. Service must not double-validate or throw.
        _repo.SetupSequence(r => r.AddAsync(It.IsAny<Haworks.Notifications.Domain.Entities.Suppression>()))
             .Returns(Task.CompletedTask)
             .Returns(Task.CompletedTask);

        var sut = BuildSut();

        await sut.AddAsync("u@example.com", NotificationChannel.Email, "complaint", null, CancellationToken.None);
        await sut.AddAsync("u@example.com", NotificationChannel.Email, "complaint", null, CancellationToken.None);

        _repo.Verify(r => r.AddAsync(It.IsAny<Haworks.Notifications.Domain.Entities.Suppression>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AddAsync_BlankRecipient_Throws(string? recipient)
    {
        var sut = BuildSut();

        var act = () => sut.AddAsync(recipient!, NotificationChannel.Email, "reason", null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AddAsync_BlankReason_Throws(string? reason)
    {
        var sut = BuildSut();

        var act = () => sut.AddAsync("u@e.com", NotificationChannel.Email, reason!, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
