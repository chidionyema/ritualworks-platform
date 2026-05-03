namespace Haworks.BuildingBlocks.CurrentUser;

/// <summary>
/// Provides access to the current user context.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>
    /// Gets the current user's ID.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Gets the client IP address.
    /// </summary>
    string? ClientIp { get; }
}
