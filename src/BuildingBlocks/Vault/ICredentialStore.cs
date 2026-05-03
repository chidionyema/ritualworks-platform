using System.Security;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Thread-safe store for database credentials with lease management.
/// </summary>
public interface ICredentialStore
{
    (string Username, SecureString Password) Current { get; }
    DateTime LeaseExpiry { get; }
    TimeSpan LeaseDuration { get; }

    Task RefreshAsync(
        Func<CancellationToken, Task<(string Username, SecureString Password, TimeSpan leaseDuration)>> refreshFunc,
        CancellationToken ct);

    bool IsExpiredOrNearExpiry(TimeSpan refreshThreshold);
}
