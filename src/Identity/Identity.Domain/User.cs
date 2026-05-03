
using Microsoft.AspNetCore.Identity;

namespace Haworks.Identity.Domain;

public class User : IdentityUser
{
    public string? CheckoutSessionId { get; set; }
    public string? StripeCustomerId { get; set; }
    public virtual UserProfile? Profile { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
