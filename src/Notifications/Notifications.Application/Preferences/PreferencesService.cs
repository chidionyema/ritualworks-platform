using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Preferences;

/// <summary>
/// L1.C preference gate. Returns the reason a notification would be blocked
/// (if any) so the caller can record an accurate suppression metric instead
/// of opaquely failing the send.
/// </summary>
/// <remarks>
/// Decision order, short-circuit on first non-Allow:
///   1. Global unsubscribe row (UserId, "*", channel) with IsEnabled=false.
///   2. Per-(category, channel) row with IsEnabled=false.
///   3. Quiet hours: window persisted on either the per-category or global
///      row, evaluated in the user's timezone.
///   4. Frequency cap: aggregate RateLimitBucket count for the user+channel
///      window vs the per-row daily cap.
///   No row at all -> Allow (default permissive).
/// </remarks>
public sealed class PreferencesService : IPreferencesService
{
    private readonly IPreferencesRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<PreferencesService> _logger;

    public PreferencesService(
        IPreferencesRepository repository,
        TimeProvider clock,
        ILogger<PreferencesService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PreferenceCheckResult> IsAllowedAsync(
        string userId,
        NotificationChannel channel,
        string category,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var rows = await _repository.GetAllForUserAsync(userId).ConfigureAwait(false);
        if (rows is null || rows.Count == 0)
        {
            // No preferences configured — default allow.
            return PreferenceCheckResult.Allow;
        }

        var globalRow = FindRow(rows, PreferenceConstants.GlobalCategory, channel);
        var categoryRow = FindRow(rows, category, channel);

        // 1. Global unsubscribe wins.
        if (globalRow is { IsEnabled: false })
        {
            return PreferenceCheckResult.Suppressed;
        }

        // 2. Per-(category, channel) opt-out.
        if (categoryRow is { IsEnabled: false })
        {
            return PreferenceCheckResult.Suppressed;
        }

        // 3. Quiet hours — category row first, then global.
        var quiet = PreferenceQuietHours.TryParse(categoryRow?.QuietHoursJson)
                  ?? PreferenceQuietHours.TryParse(globalRow?.QuietHoursJson);

        if (quiet is not null && IsInsideQuietHours(quiet))
        {
            return PreferenceCheckResult.QuietHours;
        }

        // 4. Frequency cap — category cap takes precedence over global cap.
        var cap = quiet?.DailyCap;
        if (cap is { } capValue && capValue > 0)
        {
            var bucketKey = BuildBucketKey(userId, channel, category);
            var windowStart = _clock.GetUtcNow().UtcDateTime - PreferenceConstants.DefaultRateWindow;

            var sent = await _repository.GetSendCountAsync(bucketKey, windowStart).ConfigureAwait(false);
            if (sent >= capValue)
            {
                _logger.LogInformation(
                    "Frequency cap hit. UserId: {UserId}, Channel: {Channel}, Category: {Category}, Sent: {Sent}, Cap: {Cap}",
                    userId, channel, category, sent, capValue);
                return PreferenceCheckResult.RateLimited;
            }
        }

        return PreferenceCheckResult.Allow;
    }

    private bool IsInsideQuietHours(PreferenceQuietHours quiet)
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(quiet.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            tz = TimeZoneInfo.Utc;
        }

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        return quiet.Contains(TimeOnly.FromDateTime(local));
    }

    private static NotificationPreference? FindRow(
        IReadOnlyList<NotificationPreference> rows,
        string category,
        NotificationChannel channel)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Channel == channel && string.Equals(row.Category, category, StringComparison.Ordinal))
            {
                return row;
            }
        }
        return null;
    }

    /// <summary>
    /// Bucket key used for the frequency-cap lookup. Mirrors what the send
    /// pipeline writes to <c>RateLimitBuckets.BucketKey</c>.
    /// </summary>
    public static string BuildBucketKey(string userId, NotificationChannel channel, string category) =>
        $"{userId}|{channel}|{category}";
}
