namespace Haworks.Identity.Application.Interfaces;

/// <summary>
/// Service for efficiently retrieving user email addresses with caching.
/// </summary>
public interface IUserEmailService
{
    /// <summary>
    /// Gets the email address for a user ID, using cache if available.
    /// </summary>
    Task<string?> GetUserEmailAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Removes a user's email from the cache (e.g. after an update).
    /// </summary>
    void InvalidateCache(string userId);
}
