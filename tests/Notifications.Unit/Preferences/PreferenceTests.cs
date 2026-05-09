using System.Text.Json;
using FluentAssertions;
using Haworks.Notifications.Application.Preferences;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Notifications.Unit.Preferences;

public class PreferenceTests
{
    private const string UserId = "user-1";
    private const string Category = "transactional.order";

    private readonly Mock<IPreferencesRepository> _repo = new(MockBehavior.Strict);

    private PreferencesService BuildSut(DateTimeOffset? now = null)
    {
        var clock = now is null
            ? TimeProvider.System
            : (TimeProvider)new FixedClock(now.Value);

        return new PreferencesService(_repo.Object, clock, NullLogger<PreferencesService>.Instance);
    }

    private static string QuietHoursJson(int startHour, int endHour, string tz = "UTC", int? dailyCap = null)
    {
        return JsonSerializer.Serialize(new
        {
            start = $"{startHour:D2}:00:00",
            end = $"{endHour:D2}:00:00",
            tz,
            daily_cap = dailyCap
        });
    }

    private void SetupRows(params NotificationPreference[] rows)
    {
        _repo.Setup(r => r.GetAllForUserAsync(UserId))
             .ReturnsAsync(rows);
    }

    // ─── No preferences ───────────────────────────────────────────────────

    [Fact]
    public async Task IsAllowedAsync_NoPreferences_ReturnsAllow()
    {
        SetupRows(); // empty
        var sut = BuildSut();

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
    }

    // ─── Global unsubscribe ───────────────────────────────────────────────

    [Fact]
    public async Task IsAllowedAsync_GlobalUnsubscribed_ReturnsSuppressed()
    {
        var globalOff = PreferenceFactory.Build(UserId, PreferenceConstants.GlobalCategory, NotificationChannel.Email, isEnabled: false);
        SetupRows(globalOff);

        var sut = BuildSut();

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Suppressed);
    }

    [Fact]
    public async Task IsAllowedAsync_GlobalUnsubscribed_DifferentChannel_DoesNotAffect()
    {
        // Global off for Email, but we're checking SMS — SMS should still be allowed.
        var emailGlobalOff = PreferenceFactory.Build(UserId, PreferenceConstants.GlobalCategory, NotificationChannel.Email, isEnabled: false);
        SetupRows(emailGlobalOff);

        var sut = BuildSut();

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Sms, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
    }

    // ─── Per-category opt-out ─────────────────────────────────────────────

    [Fact]
    public async Task IsAllowedAsync_CategoryDisabled_ReturnsSuppressed()
    {
        var catOff = PreferenceFactory.Build(UserId, Category, NotificationChannel.Email, isEnabled: false);
        SetupRows(catOff);

        var sut = BuildSut();

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Suppressed);
    }

    [Fact]
    public async Task IsAllowedAsync_CategoryEnabled_OtherCategoryDisabled_AllowsTarget()
    {
        var catOff = PreferenceFactory.Build(UserId, "marketing.weekly", NotificationChannel.Email, isEnabled: false);
        var catOn = PreferenceFactory.Build(UserId, Category, NotificationChannel.Email, isEnabled: true);
        SetupRows(catOff, catOn);

        var sut = BuildSut();

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
    }

    // ─── Quiet hours ──────────────────────────────────────────────────────

    [Fact]
    public async Task IsAllowedAsync_InsideQuietHours_NonWrapping_ReturnsQuietHours()
    {
        // Quiet hours 09:00-17:00 UTC. "now" is 12:00 UTC -> blocked.
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(9, 17));
        SetupRows(pref);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.QuietHours);
    }

    [Fact]
    public async Task IsAllowedAsync_OutsideQuietHours_AllowsSend()
    {
        // Quiet hours 22:00-07:00 UTC. "now" is 12:00 UTC -> allowed.
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(22, 7));
        SetupRows(pref);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
    }

    [Fact]
    public async Task IsAllowedAsync_InsideQuietHours_WrappingMidnight_ReturnsQuietHours()
    {
        // Quiet hours 22:00-07:00 UTC. "now" is 02:30 UTC -> blocked (wrap window).
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(22, 7));
        SetupRows(pref);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 2, 30, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.QuietHours);
    }

    [Fact]
    public async Task IsAllowedAsync_QuietHoursOnGlobalRow_AppliesToAllCategories()
    {
        var globalQuiet = PreferenceFactory.Build(
            UserId, PreferenceConstants.GlobalCategory, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(9, 17));
        SetupRows(globalQuiet);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.QuietHours);
    }

    // ─── Frequency cap ────────────────────────────────────────────────────

    [Fact]
    public async Task IsAllowedAsync_CapHit_ReturnsRateLimited()
    {
        // Cap is 3, already sent 3 in window — block.
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(0, 0, dailyCap: 3));
        SetupRows(pref);

        var bucketKey = PreferencesService.BuildBucketKey(UserId, NotificationChannel.Email, Category);
        _repo.Setup(r => r.GetSendCountAsync(bucketKey, It.IsAny<DateTime>()))
             .ReturnsAsync(3);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.RateLimited);
    }

    [Fact]
    public async Task IsAllowedAsync_BelowCap_Allows()
    {
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: QuietHoursJson(0, 0, dailyCap: 5));
        SetupRows(pref);

        var bucketKey = PreferencesService.BuildBucketKey(UserId, NotificationChannel.Email, Category);
        _repo.Setup(r => r.GetSendCountAsync(bucketKey, It.IsAny<DateTime>()))
             .ReturnsAsync(2);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
    }

    [Fact]
    public async Task IsAllowedAsync_NoCapConfigured_DoesNotQueryRateLimit()
    {
        var pref = PreferenceFactory.Build(
            UserId, Category, NotificationChannel.Email, isEnabled: true,
            quietHoursJson: null); // no quiet-hours payload at all
        SetupRows(pref);

        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));

        var result = await sut.IsAllowedAsync(UserId, NotificationChannel.Email, Category, CancellationToken.None);

        result.Should().Be(PreferenceCheckResult.Allow);
        _repo.Verify(r => r.GetSendCountAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    // ─── Validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IsAllowedAsync_BlankUserId_Throws(string? userId)
    {
        var sut = BuildSut();

        var act = () => sut.IsAllowedAsync(userId!, NotificationChannel.Email, Category, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
