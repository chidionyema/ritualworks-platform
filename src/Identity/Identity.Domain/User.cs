
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
    public DateTimeOffset? DeactivatedAt { get; set; }

    /// <summary>
    /// Deactivates the user account. Throws if already deactivated.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive)
            throw new InvalidOperationException("User is already deactivated.");

        IsActive = false;
        DeactivatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reactivates a previously deactivated user account. Throws if already active.
    /// </summary>
    public void Reactivate()
    {
        if (IsActive)
            throw new InvalidOperationException("User is already active.");

        IsActive = true;
        DeactivatedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}
