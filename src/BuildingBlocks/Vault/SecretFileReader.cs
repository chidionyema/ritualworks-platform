using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Reads Vault AppRole credentials from disk with optional HMAC validation.
/// </summary>
public class SecretFileReader : ISecretFileReader
{
    private readonly ILogger<SecretFileReader> _logger;

    public SecretFileReader(ILogger<SecretFileReader> logger) => _logger = logger;

    public async Task<VaultDiskSecrets> ReadSecretsAsync(
        string roleIdPath,
        string secretIdPath,
        string? hmacKeyPath,
        bool requireHmacValidation,
        CancellationToken ct)
    {
        var roleTask = ReadFileWithHmacAsync(roleIdPath, hmacKeyPath, requireHmacValidation, ct);
        var secretTask = ReadFileWithHmacAsync(secretIdPath, hmacKeyPath, requireHmacValidation, ct);
        await Task.WhenAll(roleTask, secretTask).ConfigureAwait(false);

        // Await completed tasks instead of using .Result to avoid potential deadlocks
        var (roleId, roleHmacOk) = await roleTask.ConfigureAwait(false);
        var (secretId, secretHmacOk) = await secretTask.ConfigureAwait(false);

        return new VaultDiskSecrets(roleId, secretId, roleHmacOk && secretHmacOk);
    }

    private static async Task<(string content, bool hmacValid)> ReadFileWithHmacAsync(
        string filePath,
        string? hmacKeyPath,
        bool requireValidation,
        CancellationToken ct)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var content = Encoding.UTF8.GetString(fileBytes).Trim();

        if (requireValidation && string.IsNullOrWhiteSpace(hmacKeyPath))
            throw new SecurityException($"HMAC validation required but no key path for {filePath}");

        if (string.IsNullOrWhiteSpace(hmacKeyPath) || !File.Exists(hmacKeyPath))
            return (content, false);

        var keyBytes = await File.ReadAllBytesAsync(hmacKeyPath, ct);
        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(fileBytes);
        var computedHex = Convert.ToHexString(computedHash);

        var expectedHmacPath = filePath + ".hmac";
        if (!File.Exists(expectedHmacPath))
        {
            if (requireValidation)
                throw new SecurityException($"Missing .hmac file for {filePath}");
            return (content, false);
        }

        var expectedHex = (await File.ReadAllTextAsync(expectedHmacPath, ct)).Trim();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedHex.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(expectedHex.ToLowerInvariant())))
        {
            throw new InvalidDataException($"HMAC mismatch for {filePath}");
        }

        return (content, true);
    }
}
