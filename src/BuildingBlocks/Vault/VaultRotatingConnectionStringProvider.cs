using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Background service that polls <see cref="IVaultCredentialProvider"/> every 45 seconds
/// and clears the NpgsqlDataSource pool when a password change is detected.
/// Implements <see cref="IConnectionStringProvider"/> so DbContext factories can
/// resolve the current connection string at context-creation time.
/// </summary>
public sealed class VaultRotatingConnectionStringProvider : BackgroundService, IConnectionStringProvider
{
    private readonly IVaultCredentialProvider _credentialProvider;
    private readonly string _roleName;
    private readonly string _baseConnectionString;
    private readonly ILogger<VaultRotatingConnectionStringProvider> _logger;
    private readonly TimeSpan _pollInterval;

    private string _currentPassword = string.Empty;
    private string _currentUsername = string.Empty;
    private volatile string _currentConnectionString;

    public VaultRotatingConnectionStringProvider(
        IVaultCredentialProvider credentialProvider,
        string roleName,
        string baseConnectionString,
        ILogger<VaultRotatingConnectionStringProvider> logger,
        TimeSpan? pollInterval = null)
    {
        _credentialProvider = credentialProvider;
        _roleName = roleName;
        _baseConnectionString = baseConnectionString;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(45);
        _currentConnectionString = baseConnectionString;
    }

    public string GetConnectionString() => _currentConnectionString;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial credential fetch
        await RefreshCredentialsAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RefreshCredentialsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to refresh credentials for role {RoleName}; will retry next cycle",
                    _roleName);
            }
        }
    }

    private async Task RefreshCredentialsAsync(CancellationToken ct)
    {
        var (username, password) = await _credentialProvider
            .GetDatabaseCredentialsAsync(_roleName, ct)
            .ConfigureAwait(false);

        if (string.Equals(password, _currentPassword, StringComparison.Ordinal)
            && string.Equals(username, _currentUsername, StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation(
            "Password change detected for role {RoleName}; rebuilding connection string and clearing pool",
            _roleName);

        _currentUsername = username;
        _currentPassword = password;

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Username = username,
            Password = password
        };
        _currentConnectionString = builder.ConnectionString;

        // Clear idle connections so new ones use the updated credentials.
        // NpgsqlDataSource.Clear() is not static — use ClearAllPools for the
        // connection-string-based approach.
        NpgsqlConnection.ClearAllPools();
    }
}
