using System.ComponentModel.DataAnnotations;

namespace Haworks.CheckoutOrchestrator.Application.Options;

public sealed class CheckoutOptions
{
    public const string SectionName = "Checkout";

    [Required, Url]
    public string SuccessUrl { get; set; } = string.Empty;

    [Required, Url]
    public string CancelUrl { get; set; } = string.Empty;
}
