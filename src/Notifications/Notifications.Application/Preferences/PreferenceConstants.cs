namespace Haworks.Notifications.Application.Preferences;

/// <summary>
/// Constants shared by the preferences service and repository.
/// </summary>
public static class PreferenceConstants
{
    /// <summary>
    /// Sentinel category used for the user's global preference row. A
    /// per-user row stored under this category drives the
    /// "global unsubscribed" check; a per-(category, channel) row overrides
    /// it for that specific category and channel.
    /// </summary>
    public const string GlobalCategory = "*";

    /// <summary>
    /// Lookback window for the frequency-cap check (defaults to one hour
    /// to match RateLimitBucket window granularity).
    /// </summary>
    public static readonly TimeSpan DefaultRateWindow = TimeSpan.FromHours(1);
}
