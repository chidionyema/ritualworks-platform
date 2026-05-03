namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Performs Vault AppRole logins and returns the issued client token + lease info.
///
/// Single source of truth for AppRole authentication. Used both at startup
/// (<see cref="VaultConfigBootstrap"/>) and at runtime
/// (<see cref="VaultClientFactory"/>) so the auth flow, retry policy, and
/// transport behave identically across both paths.
/// </summary>
public interface IVaultAppRoleAuthenticator
{
    Task<VaultAppRoleLoginResult> LoginAsync(
        string vaultAddress,
        string roleId,
        string secretId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an AppRole login. <see cref="LeaseDuration"/> is how long the token
/// is valid (Vault's auth.lease_duration field).
/// </summary>
public sealed record VaultAppRoleLoginResult(string ClientToken, TimeSpan LeaseDuration);
