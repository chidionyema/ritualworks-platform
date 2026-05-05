using Haworks.BuildingBlocks.Caching;
using Haworks.Identity.Application.Interfaces;
using Haworks.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Haworks.Identity.Infrastructure;

public sealed class UserEmailService(
    UserManager<User> userManager,
    IHybridCache cache,
    ILogger<UserEmailService> logger) : IUserEmailService
{
    private const string CacheKeyPrefix = "user_email:";

    public async Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var key = $"{CacheKeyPrefix}{userId}";
        
        return await cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                var user = await userManager.FindByIdAsync(userId);
                return user?.Email;
            },
            ct: ct);
    }

    public void InvalidateCache(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        var key = $"{CacheKeyPrefix}{userId}";
        // We use Task.Run because the interface is synchronous to match the monolith,
        // but the platform's cache is async. This ensures the fire-and-forget is safe.
        _ = Task.Run(async () => {
            try {
                await cache.RemoveAsync(key);
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to invalidate cache for user {UserId}", userId);
            }
        });
    }
}
