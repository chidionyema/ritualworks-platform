using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haworks.Notifications.Application.Preferences;

/// <summary>
/// Quiet-hours window persisted as JSON inside
/// <see cref="Domain.Entities.NotificationPreference.QuietHoursJson"/>.
///
/// Start/End are local-time-of-day in the user's timezone. A window of
/// 22:00 to 07:00 wraps midnight; the service handles both wrapping and
/// non-wrapping forms.
/// </summary>
public sealed record PreferenceQuietHours
{
    [JsonPropertyName("start")]
    public TimeOnly Start { get; init; }

    [JsonPropertyName("end")]
    public TimeOnly End { get; init; }

    [JsonPropertyName("tz")]
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>
    /// True iff the daily frequency cap has been hit for this preference.
    /// Optional — null means "no cap configured".
    /// </summary>
    [JsonPropertyName("daily_cap")]
    public int? DailyCap { get; init; }

    public static PreferenceQuietHours? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PreferenceQuietHours>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Contains(TimeOnly localTime)
    {
        if (Start == End)
        {
            return false; // empty window
        }

        // Non-wrapping window e.g. 09:00..17:00.
        if (Start < End)
        {
            return localTime >= Start && localTime < End;
        }

        // Wrapping window e.g. 22:00..07:00 — covers >=Start OR <End.
        return localTime >= Start || localTime < End;
    }
}
