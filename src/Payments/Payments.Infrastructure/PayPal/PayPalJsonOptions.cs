using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// Shared JSON serialization options for PayPal API communication.
/// PayPal APIs use snake_case property naming convention.
/// </summary>
internal static class PayPalJsonOptions
{
    /// <summary>
    /// Standard JSON options for PayPal API requests and responses.
    /// Uses snake_case naming, case-insensitive property matching,
    /// and ignores null values when writing.
    /// </summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
