using System.Security;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Thread-safe store for database credentials with lease management.
/// Uses SemaphoreSlim for async-safe credential refresh.
/// </summary>
public class CredentialStore : ICredentialStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Username, SecureString Password) _current = (string.Empty, new SecureString());
    private DateTime _leaseExpiry = DateTime.MinValue;
    private TimeSpan _leaseDuration = TimeSpan.Zero;

    public (string Username, SecureString Password) Current => _current;
    public DateTime LeaseExpiry => _leaseExpiry;
    public TimeSpan LeaseDuration => _leaseDuration;

    public async Task RefreshAsync(
        Func<CancellationToken, Task<(string Username, SecureString Password, TimeSpan leaseDuration)>> refreshFunc,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (username, password, leaseDuration) = await refreshFunc(ct);

            // Create a new SecureString copy
            var newPassword = new SecureString();
            foreach (var c in password.ToInsecureString())
                newPassword.AppendChar(c);
            newPassword.MakeReadOnly();

            // Dispose old password
            _current.Password?.Dispose();

            _current = (username, newPassword);
            _leaseDuration = leaseDuration;
            _leaseExpiry = DateTime.UtcNow + leaseDuration;
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsExpiredOrNearExpiry(TimeSpan refreshThreshold) =>
        DateTime.UtcNow + refreshThreshold >= _leaseExpiry;
}
