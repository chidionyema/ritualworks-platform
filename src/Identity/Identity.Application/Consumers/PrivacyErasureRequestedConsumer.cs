using Haworks.Contracts.Privacy;
using Haworks.Identity.Domain;
using Haworks.Identity.Domain.Interfaces;
using MassTransit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Identity.Application.Consumers;

/// <summary>
/// Handles GDPR erasure for identity-svc: anonymises the user's Identity
/// record and profile then publishes <see cref="PrivacyErasureCompleted"/>
/// so the Privacy saga can track completion across all bounded contexts.
/// </summary>
public sealed class PrivacyErasureRequestedConsumer(
    UserManager<User> userManager,
    IUserProfileRepository profiles,
    ILogger<PrivacyErasureRequestedConsumer> logger
) : IConsumer<PrivacyErasureRequested>
{
    public async Task Consume(ConsumeContext<PrivacyErasureRequested> context)
    {
        var msg = context.Message;
        logger.LogInformation(
            "GDPR erasure requested for UserId={UserId}, RequestId={RequestId}",
            msg.UserId, msg.RequestId);

        // Use FindByIdAsync — UserManager queries bypass EF query filters
        // when using the store directly. The user may be deactivated.
        var user = await userManager.FindByIdAsync(msg.UserId.ToString());
        if (user is not null)
        {
            // Anonymise PII fields so any audit trail referencing the row
            // no longer contains real data.
            user.UserName = $"DELETED-{msg.UserId:N}";
            user.NormalizedUserName = user.UserName.ToUpperInvariant();
            user.Email = $"deleted-{msg.UserId:N}@privacy.invalid";
            user.NormalizedEmail = user.Email.ToUpperInvariant();
            user.PhoneNumber = null;
            user.StripeCustomerId = null;
            user.CheckoutSessionId = null;
            user.IsActive = false;

            try
            {
                var result = await userManager.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                    logger.LogError("Failed to anonymise user {UserId}: {Errors}", msg.UserId, errors);

                    await context.Publish(new PrivacyErasureFailed
                    {
                        RequestId = msg.RequestId,
                        UserId = msg.UserId,
                        ServiceName = "identity-svc",
                        ErrorMessage = $"UserManager.UpdateAsync failed: {errors}"
                    });
                    return;
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Let MassTransit retry via redelivery on concurrency conflicts
                logger.LogWarning(ex,
                    "Concurrency conflict while anonymising user {UserId}, will retry", msg.UserId);
                throw;
            }
        }
        else
        {
            logger.LogInformation("User {UserId} not found — already deleted or never existed", msg.UserId);
        }

        // Anonymise the profile separately (separate table, not managed by UserManager)
        var profile = await profiles.GetByUserIdAsync(msg.UserId.ToString(), context.CancellationToken);
        if (profile is not null)
        {
            profile.UpdatePersonalInfo("DELETED", "DELETED", "");
            profile.UpdateAddress("", "", "", "", "");
            profile.UpdateProfileInfo(bio: "", website: "");
            profile.SetAvatarUrl("");
            await profiles.SaveChangesAsync(context.CancellationToken);
        }

        logger.LogInformation("User {UserId} anonymised for GDPR erasure", msg.UserId);

        await context.Publish(new PrivacyErasureCompleted
        {
            RequestId = msg.RequestId,
            UserId = msg.UserId,
            ServiceName = "identity-svc"
        });
    }
}
