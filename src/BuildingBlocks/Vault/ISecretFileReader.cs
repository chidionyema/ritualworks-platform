namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Reads secrets from disk with optional HMAC validation.
/// </summary>
public interface ISecretFileReader
{
    Task<VaultDiskSecrets> ReadSecretsAsync(
        string roleIdPath,
        string secretIdPath,
        string? hmacKeyPath,
        bool requireHmacValidation,
        CancellationToken ct);
}

/// <summary>
/// Represents secrets read from disk with HMAC validation status.
/// </summary>
public record VaultDiskSecrets(string RoleId, string SecretId, bool HmacValid);
