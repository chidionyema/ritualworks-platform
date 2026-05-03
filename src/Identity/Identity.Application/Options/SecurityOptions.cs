using System.ComponentModel.DataAnnotations;

namespace Haworks.Identity.Application.Options;

/// <summary>
/// Configuration options for security-related settings.
/// Bound from appsettings.json "Security" section.
/// </summary>
public sealed class SecurityOptions : IValidatableObject
{
    public const string SectionName = "Security";

    /// <summary>
    /// List of hosts allowed for redirect URLs in external authentication flows.
    /// Must contain at least one host in production.
    /// </summary>
    [Required]
    [MinLength(1, ErrorMessage = "At least one allowed redirect host must be configured")]
    public string[] AllowedRedirectHosts { get; set; } = Array.Empty<string>();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        for (var i = 0; i < AllowedRedirectHosts.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(AllowedRedirectHosts[i]))
            {
                yield return new ValidationResult(
                    $"AllowedRedirectHosts[{i}] cannot be empty or whitespace",
                    new[] { nameof(AllowedRedirectHosts) });
            }
        }
    }
}
