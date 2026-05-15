using System.Data.Common;
using Haworks.BuildingBlocks.Vault;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Haworks.BuildingBlocks.Persistence;

/// <summary>
/// EF Core interceptor that swaps username/password on every postgres
/// connection with Vault-issued dynamic credentials.
///
/// One instance per DbContext, bound to a Vault role at construction
/// (e.g. <c>haworks-catalog</c>). The role determines what postgres group
/// the issued ephemeral user joins, scoping access to that bounded
/// context's database only.
/// </summary>
public sealed class DynamicCredentialsConnectionInterceptor : DbConnectionInterceptor, IDisposable
{
    private readonly IVaultService _vault;
    private readonly string _roleName;
    private readonly ILogger<DynamicCredentialsConnectionInterceptor> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private (string User, string Pass)? _cachedCreds;
    private DateTime _expiry = DateTime.MinValue;
    private bool _disposed;

    public DynamicCredentialsConnectionInterceptor(
        IVaultService vault,
        string roleName,
        ILogger<DynamicCredentialsConnectionInterceptor> logger)
    {
        _vault    = vault ?? throw new ArgumentNullException(nameof(vault));
        _logger   = logger ?? throw new ArgumentNullException(nameof(logger));
        _roleName = !string.IsNullOrWhiteSpace(roleName)
            ? roleName
            : throw new ArgumentException("roleName is required.", nameof(roleName));
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DynamicCredentialsConnectionInterceptor));

        if (connection is NpgsqlConnection npgConn)
        {
            await RefreshCredentialsIfNeededAsync(cancellationToken);

            var creds = _cachedCreds!.Value;

            var builder = new NpgsqlConnectionStringBuilder(npgConn.ConnectionString)
            {
                Username = creds.User,
                Password = creds.Pass
            };

            npgConn.ConnectionString = builder.ToString();
        }

        return await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    private async Task RefreshCredentialsIfNeededAsync(CancellationToken ct)
    {
        if (_cachedCreds != null && DateTime.UtcNow < _expiry.AddSeconds(-30))
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedCreds != null && DateTime.UtcNow < _expiry.AddSeconds(-30))
                return;

            var (username, securePassword) = await _vault.GetDatabaseCredentialsAsync(_roleName, ct);
            var password = securePassword.ToInsecureString();

            _cachedCreds = (username, password);
            _expiry = DateTime.UtcNow.Add(_vault.LeaseDurationFor(_roleName));

            _logger.LogInformation(
                "Refreshed DB credentials for role {Role}. User: {User}, TTL: {Ttl}s",
                _roleName, username, _vault.LeaseDurationFor(_roleName).TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh DB credentials for role {Role}.", _roleName);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public override async Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is NpgsqlConnection npgConn && IsAuthError(eventData.Exception))
        {
            _logger.LogWarning("Auth failure detected for role {Role}. Force-clearing local cache, npgsql pool, and Vault store.", _roleName);
            _cachedCreds = null;
            NpgsqlConnection.ClearPool(npgConn);

            // The auth failure means the postgres-side ephemeral user was
            // dropped (operator revoke or Vault TTL expiry the renewal loop
            // missed). VaultService caches credentials per role too; without
            // forcing a refresh here, the next GetDatabaseCredentialsAsync
            // would hand back the same revoked username and we'd loop on
            // auth failures until VaultService's own lease check fires
            // (default 5 min before expiry — much too late).
            try
            {
                await _vault.RefreshCredentials(_roleName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception refreshEx)
            {
                _logger.LogError(refreshEx, "Failed to force-refresh Vault credentials for role {Role} after auth failure.", _roleName);
            }
        }

        await base.ConnectionFailedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAuthError(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex.Message.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("28P01", StringComparison.Ordinal))
                return true;
            ex = ex.InnerException;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
    }
}
