using System.ComponentModel.DataAnnotations;

namespace Haworks.Content.Infrastructure.Options;

/// <summary>
/// Configuration options for ClamAV virus scanning service.
/// </summary>
public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAV";

    [Required]
    [Url]
    public string RestApiUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}
